namespace Topaz.Service.Storage.Persistence;

/// <summary>
/// Transactional entity store for Azure Table-style rows. Each row is keyed by (scope, partitionKey, rowKey)
/// where scope identifies one table within one storage account. The store assigns the server Timestamp and a
/// monotonic odata.etag on every write and persists the full entity JSON. Reads are read-your-write consistent
/// and conditional writes are atomic, so a producer that reads-modifies-writes a row never races a torn or stale
/// copy - the property a distributed job sequencer assumes of real Azure Table Storage.
/// </summary>
internal interface ITableEntityStore
{
    bool Exists(string scope, string partitionKey, string rowKey);

    /// <summary>Returns the stored entity JSON or null when absent.</summary>
    string? Get(string scope, string partitionKey, string rowKey);

    /// <summary>Inserts a new entity; throws if (pk,rk) already exists. Returns the stored JSON.</summary>
    string Insert(string scope, string partitionKey, string rowKey, string bodyJson);

    /// <summary>Inserts or replaces unconditionally (InsertOrReplace). Returns the stored JSON.</summary>
    string Upsert(string scope, string partitionKey, string rowKey, string bodyJson);

    /// <summary>
    /// Conditionally updates an existing entity in one transaction. <paramref name="merge"/> overlays properties;
    /// otherwise replaces. Throws when absent or when the If-Match precondition fails.
    /// </summary>
    void Update(string scope, string partitionKey, string rowKey, string bodyJson, string ifMatch, bool merge);

    /// <summary>Conditionally deletes; throws when absent or when If-Match fails.</summary>
    void Delete(string scope, string partitionKey, string rowKey, string ifMatch);

    /// <summary>All entity bodies in a scope, ordered by (PartitionKey, RowKey) for deterministic paging.</summary>
    IReadOnlyList<string> List(string scope);
}
