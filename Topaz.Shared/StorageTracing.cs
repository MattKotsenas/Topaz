namespace Topaz.Shared;

/// <summary>
/// Decoupled tracing hook for table storage operations. The storage service (which cannot reference the host)
/// calls <see cref="RecordTableOp"/>; the host registers <see cref="OnTableOp"/> at startup to forward these to
/// the OpenTelemetry span file. This surfaces the read/modify/write etag timeline - and whether a mutation
/// returned an ETag response header - in the same trace as request spans, for both file- and SQLite-backed
/// storage. No-op until a sink is registered.
/// </summary>
public static class StorageTracing
{
    public sealed record TableOp(
        string Op,
        string Table,
        string PartitionKey,
        string RowKey,
        string? StoredEtag,
        bool ResponseHadEtag,
        string? TraceParent);

    /// <summary>Sink registered by the host (forwards to the OTel span writer). Null = tracing off.</summary>
    public static Action<TableOp>? OnTableOp;

    public static bool IsEnabled => OnTableOp is not null;

    public static void RecordTableOp(
        string op,
        string table,
        string partitionKey,
        string rowKey,
        string? storedEtag,
        bool responseHadEtag,
        string? traceParent = null)
    {
        var sink = OnTableOp;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(new TableOp(op, table, partitionKey, rowKey, storedEtag, responseHadEtag, traceParent));
        }
        catch
        {
            // Telemetry must never affect request processing.
        }
    }
}
