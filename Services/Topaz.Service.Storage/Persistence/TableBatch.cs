namespace Topaz.Service.Storage.Persistence;

/// <summary>The kind of a single operation inside an Entity Group Transaction ($batch changeset).</summary>
internal enum TableBatchOp
{
    Insert,
    Merge,
    Replace,
    Delete,
    Retrieve,
}

/// <summary>
/// One operation in an Entity Group Transaction. <paramref name="UpsertOnMissing"/> is the SDK's
/// InsertOrMerge / InsertOrReplace behaviour (a Merge/Replace on a missing entity inserts it instead of failing).
/// </summary>
internal sealed record TableBatchAction(
    TableBatchOp Op,
    string Scope,
    string PartitionKey,
    string RowKey,
    string? BodyJson,
    string IfMatch,
    bool UpsertOnMissing);

/// <summary>Per-operation result: the new etag for writes, the stored body for a retrieve.</summary>
internal sealed record TableBatchResult(string? Etag, string? Body);

/// <summary>
/// Raised when one operation in a $batch changeset fails its precondition (or insert/lookup), so the whole
/// transaction was rolled back. <see cref="Index"/> identifies the failing operation (Azure returns a single
/// changeset error keyed to it).
/// </summary>
internal sealed class TableBatchConflictException(int index, System.Exception cause)
    : System.Exception(cause.Message, cause)
{
    public int Index { get; } = index;
}
