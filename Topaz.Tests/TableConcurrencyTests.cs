using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Service.Storage.Persistence;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Direct, host-free coverage of the table data-plane under concurrent writers and readers of the SAME entity.
/// The data plane is backed by the SQLite transactional store: a read-modify-write runs in one transaction and
/// reads observe only committed state, so a concurrent reader can never see a torn, empty, or missing entity - the
/// failure mode the earlier file store allowed under a non-atomic truncate-then-write. A high-frequency writer
/// (e.g. a fast job-poll updating one row) overlapping a reader reproduced the old corruption; these tests prove
/// the SQLite substrate eliminates it, both through the data plane and from an independent connection observing the
/// persisted rows directly.
/// </summary>
[TestFixture]
public class TableConcurrencyTests
{
    private static IHeaderDictionary Unconditional()
        => new HeaderDictionary { ["If-Match"] = "*" };

    private static System.IO.Stream Body(string json)
        => new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));

    [Test]
    public void ConcurrentUpdatesAndReads_NeverObserveTornOrMissingEntity()
    {
        var logger = new PrettyTopazLogger();
        var provider = new TableResourceProvider(logger);
        var dataPlane = new TableServiceDataPlane(provider, logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string table = "ConcurrencyTable";
        const string pk = "pk1";
        const string rk = "rk1";

        var dataDir = provider.GetTableDataPath(subscription, resourceGroup, table, account);
        Directory.CreateDirectory(dataDir);

        const int writerTasks = 4;
        const int updatesPerWriter = 150;
        const int readerTasks = 4;

        try
        {
            // Seed the single contended entity.
            dataPlane.InsertEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"0"}"""),
                subscription, resourceGroup, table, account);

            var readerErrors = new ConcurrentBag<Exception>();
            var writerErrors = new ConcurrentBag<Exception>();
            var reads = 0;
            var writesDone = 0;
            using var writersComplete = new CountdownEvent(writerTasks);

            var writers = Enumerable.Range(0, writerTasks).Select(w => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < updatesPerWriter; i++)
                    {
                        // Unconditional update on the SAME entity from every writer = maximum write
                        // contention on one file, which is exactly the fast-writer-vs-reader overlap.
                        // Pre-fix, one writer's File.Delete made another's File.ReadAllText throw here.
                        dataPlane.UpdateEntity(
                            Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"{{i}}","Writer":"{{w}}"}"""),
                            subscription, resourceGroup, table, account, pk, rk, Unconditional());
                        Interlocked.Increment(ref writesDone);
                    }
                }
                catch (Exception ex)
                {
                    writerErrors.Add(ex);
                }
                finally
                {
                    writersComplete.Signal();
                }
            })).ToArray();

            var readers = Enumerable.Range(0, readerTasks).Select(_ => Task.Run(() =>
            {
                while (!writersComplete.IsSet)
                {
                    try
                    {
                        // A torn write would make this Parse throw; a delete-then-write gap would make
                        // GetEntity throw EntityNotFound. Either is the corruption we are guarding against.
                        var json = dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rk);
                        var node = JsonNode.Parse(json)!.AsObject();
                        // The keys must always be present and intact in any consistent snapshot.
                        if ((string?)node["PartitionKey"] != pk || (string?)node["RowKey"] != rk)
                        {
                            readerErrors.Add(new InvalidOperationException(
                                "Reader observed an entity with wrong/missing keys: " + json));
                        }

                        Interlocked.Increment(ref reads);
                    }
                    catch (Exception ex)
                    {
                        readerErrors.Add(ex);
                    }
                }
            })).ToArray();

            Task.WaitAll(writers.Concat(readers).ToArray(), TimeSpan.FromSeconds(60));

            // The test must actually have exercised the race, otherwise a clean result is vacuous.
            Assert.That(writerErrors, Is.Empty,
                "no writer may fail under concurrent same-entity updates; first error: "
                + (writerErrors.FirstOrDefault()?.ToString() ?? "<none>"));
            Assert.That(writesDone, Is.EqualTo(writerTasks * updatesPerWriter),
                "precondition: all writes completed");
            Assert.That(reads, Is.GreaterThan(0),
                "precondition: readers actually read the contended entity during the writes");

            Assert.That(readerErrors, Is.Empty,
                "no reader may observe a torn/empty/missing entity under concurrent writes; first error: "
                + (readerErrors.FirstOrDefault()?.ToString() ?? "<none>"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); }
            catch (IOException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// The durability invariant from an INDEPENDENT connection: a second SQLite connection observing the persisted
    /// rows directly (bypassing the writer's in-process gate) never sees the contended entity's body empty or partial
    /// under concurrent writes, and the row is left complete + parseable after the storm. The earlier file store could
    /// leave a 0-byte file after a truncate-then-write was interrupted mid-write - the exact corruption that stranded a
    /// background job and wedged a consumer deployment. SQLite commits each write atomically, so a cross-connection read
    /// only ever sees a whole committed body. (The sibling test reads through the data plane's own connection; this one
    /// reads from a separate connection, proving the durability holds across connections, not just under the gate.)
    /// </summary>
    [Test]
    public void ConcurrentWrites_CommittedEntityIsNeverObservedTornOrEmpty()
    {
        var logger = new PrettyTopazLogger();
        var provider = new TableResourceProvider(logger);
        var dbDir = Path.Combine(Path.GetTempPath(), "topaz-atomic-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(dbDir, "t.db");
        var store = SqliteTableEntityStore.ForDatabase(dbPath);
        var dataPlane = new TableServiceDataPlane(provider, logger, store);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string table = "AtomicTable";
        const string pk = "pk1";
        const string rk = "rk1";

        var scope = provider.GetTableDataPath(subscription, resourceGroup, table, account);

        const int writerTasks = 4;
        const int updatesPerWriter = 200;

        try
        {
            dataPlane.InsertEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"0"}"""),
                subscription, resourceGroup, table, account);

            var observerErrors = new ConcurrentBag<Exception>();
            var observations = 0;
            var writesDone = 0;
            var writerErrors = new ConcurrentBag<Exception>();
            using var writersComplete = new CountdownEvent(writerTasks);

            var writers = Enumerable.Range(0, writerTasks).Select(w => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < updatesPerWriter; i++)
                    {
                        // Unconditional update on the SAME entity from every writer = maximum write contention on
                        // one row, the fast-writer-vs-reader overlap. The store serialises commits; no write may fail.
                        dataPlane.UpdateEntity(
                            Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"{{i}}","Writer":"{{w}}"}"""),
                            subscription, resourceGroup, table, account, pk, rk, Unconditional());
                        Interlocked.Increment(ref writesDone);
                    }
                }
                catch (Exception ex) { writerErrors.Add(ex); }
                finally { writersComplete.Signal(); }
            })).ToArray();

            // A single observer reads the persisted body from its OWN read-only SQLite connection - NOT through the
            // data plane, so it is not serialised with the writers by the store's in-process gate. WAL lets it read
            // committed state concurrently. A non-atomic write would expose a 0-byte / partial body window to it;
            // SQLite's atomic commit never does.
            var observers = Enumerable.Range(0, 1).Select(_ => Task.Run(() =>
            {
                using var read = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared,
                }.ToString());
                read.Open();
                while (!writersComplete.IsSet)
                {
                    try
                    {
                        using var cmd = read.CreateCommand();
                        cmd.CommandText = "SELECT body FROM entities WHERE scope=$s AND pk=$p AND rk=$r;";
                        cmd.Parameters.AddWithValue("$s", scope);
                        cmd.Parameters.AddWithValue("$p", pk);
                        cmd.Parameters.AddWithValue("$r", rk);
                        var observed = cmd.ExecuteScalar() as string;
                        if (string.IsNullOrEmpty(observed))
                        {
                            observerErrors.Add(new InvalidOperationException(
                                "the contended entity was observed empty/missing in committed state"));
                            continue;
                        }

                        JsonNode.Parse(observed); // a partial body => JsonException
                        Interlocked.Increment(ref observations);
                    }
                    catch (System.Text.Json.JsonException ex) { observerErrors.Add(ex); }

                    Thread.Sleep(1);
                }
            })).ToArray();

            Task.WaitAll(writers.Concat(observers).ToArray(), TimeSpan.FromSeconds(60));

            Assert.That(writerErrors, Is.Empty,
                "no writer may fail under concurrent same-entity updates; first error: "
                + (writerErrors.FirstOrDefault()?.ToString() ?? "<none>"));
            Assert.That(writesDone, Is.EqualTo(writerTasks * updatesPerWriter), "precondition: all writes completed");
            Assert.That(observations, Is.GreaterThan(0),
                "precondition: the observer actually read committed state during the writes");

            Assert.That(observerErrors, Is.Empty,
                "the committed entity body must never be observed empty or partial from an independent connection; first error: "
                + (observerErrors.FirstOrDefault()?.ToString() ?? "<none>"));

            // And it must be left complete + parseable after the storm (a persistent empty entity is the stall).
            var finalBody = dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rk);
            Assert.That(finalBody, Is.Not.Null.And.Not.Empty, "the entity must not be left empty");
            Assert.That(() => JsonNode.Parse(finalBody), Throws.Nothing,
                "the final entity must be valid JSON (a torn write would strand any reader of this entity)");
        }
        finally
        {
            try { Directory.Delete(dbDir, recursive: true); }
            catch (IOException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
        }
    }
}
