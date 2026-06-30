using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Topaz.Service.Storage.Exceptions;
using Topaz.Shared;

namespace Topaz.Service.Storage.Persistence;

/// <summary>
/// SQLite-backed <see cref="ITableEntityStore"/>. One WAL database holds every table's rows in an
/// <c>entities(scope, pk, rk, ts, etag, body)</c> table keyed by (scope, pk, rk). All conditional operations run
/// inside a single transaction under a process lock, so read-modify-write is atomic and reads observe only
/// committed state - eliminating the torn-file/lost-update/stale-snapshot races the file store allowed. The stored
/// body preserves the exact entity JSON (PartitionKey, RowKey, Timestamp, odata.etag) so endpoint and SDK behaviour
/// is unchanged. ETags are monotonic ticks.
/// </summary>
internal sealed class SqliteTableEntityStore : ITableEntityStore
{
    private static readonly Dictionary<string, SqliteTableEntityStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object StoresLock = new();
    private static long _lastEtagTicks;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    private SqliteTableEntityStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(dir);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        Execute("CREATE TABLE IF NOT EXISTS entities (scope TEXT NOT NULL, pk TEXT NOT NULL, rk TEXT NOT NULL, ts TEXT NOT NULL, etag TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY (scope, pk, rk));");
    }

    /// <summary>One store per database file; the default db lives under the emulator root.</summary>
    public static SqliteTableEntityStore Default => ForDatabase(Path.Combine(GlobalSettings.MainEmulatorDirectory, ".topaz-storage", "tables.db"));

    public static SqliteTableEntityStore ForDatabase(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        lock (StoresLock)
        {
            if (!Stores.TryGetValue(full, out var store))
            {
                store = new SqliteTableEntityStore(full);
                Stores[full] = store;
            }

            return store;
        }
    }

    public bool Exists(string scope, string partitionKey, string rowKey)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM entities WHERE scope=$s AND pk=$p AND rk=$r LIMIT 1;";
            Bind(cmd, scope, partitionKey, rowKey);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public string? Get(string scope, string partitionKey, string rowKey)
    {
        lock (_gate)
        {
            return ReadBody(scope, partitionKey, rowKey);
        }
    }

    public string Insert(string scope, string partitionKey, string rowKey, string bodyJson)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            if (ReadBody(scope, partitionKey, rowKey, tx) is not null)
                throw new EntityAlreadyExistsException();
            var stored = Stamp(bodyJson, partitionKey, rowKey, out var ts, out var etag);
            Write(scope, partitionKey, rowKey, ts, etag, stored, tx);
            tx.Commit();
            return stored;
        }
    }

    public string Upsert(string scope, string partitionKey, string rowKey, string bodyJson)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var stored = Stamp(bodyJson, partitionKey, rowKey, out var ts, out var etag);
            Write(scope, partitionKey, rowKey, ts, etag, stored, tx);
            tx.Commit();
            return stored;
        }
    }

    public string Update(string scope, string partitionKey, string rowKey, string bodyJson, string ifMatch, bool merge)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var existing = ReadBody(scope, partitionKey, rowKey, tx) ?? throw new EntityNotFoundException();
            EtagPrecondition.EnsureSatisfied(ifMatch, existing);
            var incoming = JsonNode.Parse(bodyJson)!.AsObject();
            JsonObject result = incoming;
            if (merge && JsonNode.Parse(existing) is JsonObject current)
            {
                foreach (var property in incoming)
                    current[property.Key] = property.Value?.DeepClone();
                result = current;
            }

            var stored = Stamp(result.ToJsonString(), partitionKey, rowKey, out var ts, out var etag);
            Write(scope, partitionKey, rowKey, ts, etag, stored, tx);
            tx.Commit();
            return etag;
        }
    }

    public void Delete(string scope, string partitionKey, string rowKey, string ifMatch)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var existing = ReadBody(scope, partitionKey, rowKey, tx) ?? throw new EntityNotFoundException();
            EtagPrecondition.EnsureSatisfied(ifMatch, existing);
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM entities WHERE scope=$s AND pk=$p AND rk=$r;";
            Bind(cmd, scope, partitionKey, rowKey);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public IReadOnlyList<string> List(string scope)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT body FROM entities WHERE scope=$s ORDER BY pk ASC, rk ASC;";
            cmd.Parameters.AddWithValue("$s", scope);
            using var reader = cmd.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(reader.GetString(0));
            return rows;
        }
    }

    public IReadOnlyList<TableBatchResult> ExecuteBatch(IReadOnlyList<TableBatchAction> actions)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var results = new List<TableBatchResult>(actions.Count);
            for (var i = 0; i < actions.Count; i++)
            {
                try
                {
                    results.Add(ApplyInTransaction(actions[i], tx));
                }
                catch (Exception ex)
                {
                    // ANY failed operation rolls the whole changeset back (all-or-nothing EGT). Precondition/
                    // insert/lookup failures map to 409/404/412; anything else (e.g. a malformed entity body) maps
                    // to 400 - but either way the transaction is rolled back and the batch reports the failed index.
                    tx.Rollback();
                    throw new TableBatchConflictException(i, ex);
                }
            }

            tx.Commit();
            return results;
        }
    }

    private TableBatchResult ApplyInTransaction(TableBatchAction a, SqliteTransaction tx)
    {
        switch (a.Op)
        {
            case TableBatchOp.Retrieve:
                return new TableBatchResult(null,
                    ReadBody(a.Scope, a.PartitionKey, a.RowKey, tx) ?? throw new EntityNotFoundException());

            case TableBatchOp.Insert:
            {
                if (ReadBody(a.Scope, a.PartitionKey, a.RowKey, tx) is not null)
                    throw new EntityAlreadyExistsException();
                var stored = Stamp(a.BodyJson!, a.PartitionKey, a.RowKey, out var ts, out var etag);
                Write(a.Scope, a.PartitionKey, a.RowKey, ts, etag, stored, tx);
                return new TableBatchResult(etag, stored);
            }

            case TableBatchOp.Merge:
            case TableBatchOp.Replace:
            {
                var existing = ReadBody(a.Scope, a.PartitionKey, a.RowKey, tx);
                if (existing is null)
                {
                    // InsertOrMerge / InsertOrReplace: the SDK's unconditional upsert inserts a missing entity.
                    if (!a.UpsertOnMissing) throw new EntityNotFoundException();
                    var inserted = Stamp(a.BodyJson!, a.PartitionKey, a.RowKey, out var its, out var ietag);
                    Write(a.Scope, a.PartitionKey, a.RowKey, its, ietag, inserted, tx);
                    return new TableBatchResult(ietag, inserted);
                }

                EtagPrecondition.EnsureSatisfied(a.IfMatch, existing);
                var incoming = JsonNode.Parse(a.BodyJson!)!.AsObject();
                JsonObject body = incoming;
                if (a.Op == TableBatchOp.Merge && JsonNode.Parse(existing) is JsonObject current)
                {
                    foreach (var property in incoming)
                        current[property.Key] = property.Value?.DeepClone();
                    body = current;
                }

                var stored = Stamp(body.ToJsonString(), a.PartitionKey, a.RowKey, out var ts, out var etag);
                Write(a.Scope, a.PartitionKey, a.RowKey, ts, etag, stored, tx);
                return new TableBatchResult(etag, stored);
            }

            case TableBatchOp.Delete:
            {
                var existing = ReadBody(a.Scope, a.PartitionKey, a.RowKey, tx) ?? throw new EntityNotFoundException();
                EtagPrecondition.EnsureSatisfied(a.IfMatch, existing);
                DeleteRow(a.Scope, a.PartitionKey, a.RowKey, tx);
                return new TableBatchResult(null, null);
            }

            default:
                throw new InvalidOperationException("Unknown batch op " + a.Op);
        }
    }

    private void DeleteRow(string scope, string pk, string rk, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM entities WHERE scope=$s AND pk=$p AND rk=$r;";
        Bind(cmd, scope, pk, rk);
        cmd.ExecuteNonQuery();
    }

    private string? ReadBody(string scope, string pk, string rk, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT body FROM entities WHERE scope=$s AND pk=$p AND rk=$r;";
        Bind(cmd, scope, pk, rk);
        return cmd.ExecuteScalar() as string;
    }

    private void Write(string scope, string pk, string rk, string ts, string etag, string body, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO entities (scope, pk, rk, ts, etag, body) VALUES ($s,$p,$r,$ts,$e,$b) " +
                          "ON CONFLICT(scope, pk, rk) DO UPDATE SET ts=$ts, etag=$e, body=$b;";
        Bind(cmd, scope, pk, rk);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$e", etag);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, string scope, string pk, string rk)
    {
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$p", pk);
        cmd.Parameters.AddWithValue("$r", rk);
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Stamp the server-authoritative keys, a monotonic Timestamp, and the matching Azure-format weak ETag onto the
    // body. Azure Table derives the ETag FROM the entity Timestamp - W/"datetime'<url-encoded Timestamp>'" - and
    // reports that one value identically on every surface: the write-response ETag header, a $batch sub-response ETag,
    // and the odata.etag in a GET/query body. A consumer doing read-modify-write (compare the ETag a write returned to
    // the odata.etag a later read returns) depends on those being byte-identical. Topaz previously stamped a bare
    // monotonic-ticks ETag ("<ticks>") on the stored body while the $batch INSERT path synthesized the Azure
    // W/"datetime'...'" form from the Timestamp - two different strings for the same write - so a distributed job
    // sequencer's optimistic-concurrency check (snapshot ETag vs. re-read odata.etag) NEVER matched and churned
    // forever. Deriving the single Azure-format value here makes INSERT, MERGE/REPLACE and GET agree. The Timestamp is
    // advanced to a strictly-increasing tick (UtcNow, or previous+1 when writes land in the same tick) so the derived
    // ETag stays unique for optimistic concurrency.
    private static string Stamp(string bodyJson, string pk, string rk, out string ts, out string etag)
    {
        var root = JsonNode.Parse(bodyJson)!.AsObject();
        var ticks = NextTimestampTicks();
        ts = new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'");
        etag = "W/\"datetime'" + Uri.EscapeDataString(ts) + "'\"";
        root["PartitionKey"] = pk;
        root["RowKey"] = rk;
        root["Timestamp"] = ts;
        root["odata.etag"] = etag;
        return root.ToJsonString();
    }

    private static long NextTimestampTicks()
    {
        long ticks;
        long previous;
        do
        {
            previous = Interlocked.Read(ref _lastEtagTicks);
            ticks = Math.Max(DateTimeOffset.UtcNow.Ticks, previous + 1);
        }
        while (Interlocked.CompareExchange(ref _lastEtagTicks, ticks, previous) != previous);
        return ticks;
    }
}
