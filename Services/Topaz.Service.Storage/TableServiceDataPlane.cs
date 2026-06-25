using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Topaz.Service.Storage.Exceptions;
using Topaz.Service.Storage.Models;
using Topaz.Service.Storage.OData;
using Topaz.Shared;
using Microsoft.AspNetCore.Http;
using Azure;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;

namespace Topaz.Service.Storage;

internal sealed class TableServiceDataPlane(TableResourceProvider resourceProvider, ITopazLogger logger)
{
    internal string InsertEntity(Stream input, SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName)
    {
        logger.LogDebug(nameof(TableServiceDataPlane), nameof(InsertEntity), "Executing {0}: {1} {2}", nameof(InsertEntity), tableName, storageAccountName);

        var path = resourceProvider.GetTableDataPath(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName);

        using var sr = new StreamReader(input);

        var rawContent = sr.ReadToEnd();
        var metadata = JsonSerializer.Deserialize<GenericTableEntity>(rawContent, GlobalSettings.JsonOptions) ?? throw new Exception();

        logger.LogDebug(nameof(TableServiceDataPlane), nameof(InsertEntity), "Executing {0}: Inserting {1}.", nameof(InsertEntity), rawContent);

        var safePartitionKey = PathGuard.SanitizeName(metadata.PartitionKey!);
        var safeRowKey = PathGuard.SanitizeName(metadata.RowKey!);

        var etag = new ETag(DateTimeOffset.Now.Ticks.ToString());
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff'Z'");
        var fileName = $"{safePartitionKey}_{safeRowKey}.json";
        var entityPath = Path.Combine(path, fileName);
        PathGuard.EnsureWithinDirectory(entityPath, path);

        if(File.Exists(entityPath))
        {
            // Duplicated entry
            logger.LogDebug(nameof(TableServiceDataPlane), nameof(InsertEntity), "Executing {0}: Duplicated entry.", nameof(InsertEntity));
            throw new EntityAlreadyExistsException();
        } 

        var root = JsonNode.Parse(rawContent);
        root!["Timestamp"] = timestamp;
        root!["odata.etag"] = etag.ToString("H");

        var data = root.ToJsonString();

        lock (EntityLock(entityPath))
        {
            // Re-check existence under the lock so a concurrent insert cannot slip between the check
            // above and the write below.
            if (File.Exists(entityPath))
            {
                logger.LogDebug(nameof(TableServiceDataPlane), nameof(InsertEntity), "Executing {0}: Duplicated entry.", nameof(InsertEntity));
                throw new EntityAlreadyExistsException();
            }

            File.WriteAllText(entityPath, data);
        }

        return rawContent;
    }

    internal string GetEntity(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName,
        string partitionKey, string rowKey)
    {
        logger.LogDebug(nameof(TableServiceDataPlane), nameof(GetEntity), "Executing {0}: {1} {2}", nameof(GetEntity), tableName, storageAccountName);

        PathGuard.ValidateName(partitionKey);
        PathGuard.ValidateName(rowKey);

        var path = resourceProvider.GetTableDataPath(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName);

        var fileName = $"{PathGuard.SanitizeName(partitionKey)}_{PathGuard.SanitizeName(rowKey)}.json";
        var entityPath = Path.Combine(path, fileName);
        PathGuard.EnsureWithinDirectory(entityPath, path);

        if (!File.Exists(entityPath))
        {
            throw new EntityNotFoundException();
        }

        lock (EntityLock(entityPath))
        {
            // Re-check under the lock: a concurrent delete may have removed it after the check above.
            if (!File.Exists(entityPath))
            {
                throw new EntityNotFoundException();
            }

            return File.ReadAllText(entityPath);
        }
    }

    internal TableQueryResult QueryEntities(QueryString query, SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName)
    {
        logger.LogDebug(nameof(TableServiceDataPlane), nameof(QueryEntities), "Executing {0}: {1} {2} {3}", nameof(QueryEntities), query, tableName, storageAccountName);

        var options = TableODataQueryOptions.Parse(query);

        var path = resourceProvider.GetTableDataPath(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName);

        // Load all entities from disk as JsonObject so we can inspect individual properties.
        var allEntities = Directory.EnumerateFiles(path, "*.json")
            .Select(file =>
            {
                // Lock the individual entity file so the read never overlaps a writer's rewrite, and
                // tolerate a concurrent delete between enumeration and read (the file is simply gone).
                string? content = null;
                lock (EntityLock(file))
                {
                    if (File.Exists(file))
                        content = File.ReadAllText(file);
                }

                return content is null ? null : JsonSerializer.Deserialize<JsonObject>(content, GlobalSettings.JsonOptions);
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        // Sort by (PartitionKey ASC, RowKey ASC) for deterministic, stable paging.
        allEntities.Sort((a, b) =>
        {
            var pkA = a["PartitionKey"]?.GetValue<string>() ?? string.Empty;
            var pkB = b["PartitionKey"]?.GetValue<string>() ?? string.Empty;
            var pkCmp = string.Compare(pkA, pkB, StringComparison.Ordinal);
            if (pkCmp != 0) return pkCmp;
            var rkA = a["RowKey"]?.GetValue<string>() ?? string.Empty;
            var rkB = b["RowKey"]?.GetValue<string>() ?? string.Empty;
            return string.Compare(rkA, rkB, StringComparison.Ordinal);
        });

        // Apply continuation seek: skip entities that precede the (NextPartitionKey, NextRowKey) cursor.
        IEnumerable<JsonObject> sequence = allEntities;
        if (options.NextPartitionKey is not null)
        {
            var nextPk = options.NextPartitionKey;
            var nextRk = options.NextRowKey ?? string.Empty;
            sequence = sequence.SkipWhile(e =>
            {
                var pk = e["PartitionKey"]?.GetValue<string>() ?? string.Empty;
                var rk = e["RowKey"]?.GetValue<string>() ?? string.Empty;
                var pkCmp = string.Compare(pk, nextPk, StringComparison.Ordinal);
                return pkCmp < 0 || (pkCmp == 0 && string.Compare(rk, nextRk, StringComparison.Ordinal) < 0);
            });
        }

        // Apply $filter.
        if (options.Filter is not null)
            sequence = sequence.Where(e => TableODataFilter.Evaluate(options.Filter, e));

        // Materialise so we can index for paging.
        var filtered = sequence.ToList();

        // Determine page boundary and populate continuation keys when more entities follow.
        string? contNextPk = null;
        string? contNextRk = null;
        List<JsonObject> page;

        if (options.Top.HasValue && filtered.Count > options.Top.Value)
        {
            page = filtered.Take(options.Top.Value).ToList();
            var firstExcluded = filtered[options.Top.Value];
            contNextPk = firstExcluded["PartitionKey"]?.GetValue<string>();
            contNextRk = firstExcluded["RowKey"]?.GetValue<string>();
        }
        else
        {
            page = filtered;
        }

        // Apply $select projection: create a new JsonObject containing only the requested properties.
        object?[] result;
        if (options.Select is not null)
        {
            result = page
                .Select(e =>
                {
                    var projected = new JsonObject();
                    foreach (var prop in options.Select)
                    {
                        if (e.TryGetPropertyValue(prop, out var value))
                            projected[prop] = value?.DeepClone();
                    }
                    return (object?)projected;
                })
                .ToArray();
        }
        else
        {
            result = page.Cast<object?>().ToArray();
        }

        return new TableQueryResult(result, contNextPk, contNextRk);
    }

    internal void DeleteEntity(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName,
        string partitionKey, string rowKey, IHeaderDictionary headers)
    {
        var etag = headers["If-Match"].FirstOrDefault() ?? "*";
        DeleteEntity(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName,
            partitionKey, rowKey, etag);
    }

    internal void DeleteEntity(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName,
        string partitionKey, string rowKey, string etag = "*")
    {
        logger.LogDebug(nameof(TableServiceDataPlane), nameof(DeleteEntity), "Executing {0}: {1} {2}", nameof(DeleteEntity), tableName, storageAccountName);

        PathGuard.ValidateName(partitionKey);
        PathGuard.ValidateName(rowKey);

        var path = resourceProvider.GetTableDataPath(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName);

        var fileName = $"{PathGuard.SanitizeName(partitionKey)}_{PathGuard.SanitizeName(rowKey)}.json";
        var entityPath = Path.Combine(path, fileName);
        PathGuard.EnsureWithinDirectory(entityPath, path);

        if (!File.Exists(entityPath))
        {
            logger.LogDebug(nameof(TableServiceDataPlane), nameof(DeleteEntity), "Executing {0}: Entity not found.", nameof(DeleteEntity));
            throw new EntityNotFoundException();
        }

        lock (EntityLock(entityPath))
        {
            if (!File.Exists(entityPath))
            {
                logger.LogDebug(nameof(TableServiceDataPlane), nameof(DeleteEntity), "Executing {0}: Entity not found.", nameof(DeleteEntity));
                throw new EntityNotFoundException();
            }

            if (etag != "*")
            {
                var node = JsonNode.Parse(File.ReadAllText(entityPath));
                if (!EtagMatches(etag, node?["odata.etag"]?.GetValue<string>(), node?["Timestamp"]?.GetValue<string>()))
                    throw new UpdateConditionNotSatisfiedException();
            }

            File.Delete(entityPath);
        }
    }

    // Decide whether an If-Match precondition is satisfied for a stored entity. Two etag shapes occur:
    //  1. The stored odata.etag (ETag.ToString("H") => "\"<ticks>\"") - matched directly (quote/weak-prefix
    //     insensitive).
    //  2. The legacy Cosmos.Table SDK derives the conditional etag from the entity TIMESTAMP, not odata.etag,
    //     sending If-Match: W/"datetime'<url-encoded-timestamp>'" (classic Table Storage protocol). Match that
    //     against the stored Timestamp. Without this, a conditional merge/update against an existing entity
    //     always 412s because the SDK's timestamp-etag never equals the ticks-based odata.etag.
    private static bool EtagMatches(string ifMatch, string? storedEtag, string? storedTimestamp)
    {
        if (string.IsNullOrEmpty(ifMatch)) return false;
        if (NormalizeETag(storedEtag) == NormalizeETag(ifMatch)) return true;

        var marker = "datetime'";
        var idx = ifMatch.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && !string.IsNullOrEmpty(storedTimestamp))
        {
            var start = idx + marker.Length;
            var end = ifMatch.IndexOf('\'', start);
            if (end > start)
            {
                var decoded = Uri.UnescapeDataString(ifMatch.Substring(start, end - start));
                if (string.Equals(decoded, storedTimestamp, StringComparison.Ordinal)) return true;
                if (DateTimeOffset.TryParse(decoded, out var a) &&
                    DateTimeOffset.TryParse(storedTimestamp, out var b) &&
                    a.UtcDateTime == b.UtcDateTime) return true;
            }
        }

        return false;
    }

    private static string NormalizeETag(string? etag)
    {
        if (string.IsNullOrEmpty(etag)) return string.Empty;
        var e = etag!.Trim();
        if (e.StartsWith("W/", StringComparison.Ordinal)) e = e.Substring(2);
        return e.Trim('"');
    }

    internal void UpsertEntity(Stream input, SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName,
        string partitionKey, string rowKey)
    {
        logger.LogDebug(nameof(TableServiceDataPlane), nameof(UpsertEntity), "Executing {0}: {1} {2}", nameof(UpsertEntity), tableName, storageAccountName);

        PathGuard.ValidateName(partitionKey);
        PathGuard.ValidateName(rowKey);

        var path = resourceProvider.GetTableDataPath(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName);

        using var sr = new StreamReader(input);
        var rawContent = sr.ReadToEnd();

        var fileName = $"{PathGuard.SanitizeName(partitionKey)}_{PathGuard.SanitizeName(rowKey)}.json";
        var entityPath = Path.Combine(path, fileName);
        PathGuard.EnsureWithinDirectory(entityPath, path);

        var root = JsonNode.Parse(rawContent) ?? throw new Exception("Cannot parse entity body.");
        root["PartitionKey"] = partitionKey;
        root["RowKey"] = rowKey;
        root["Timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff'Z'");
        root["odata.etag"] = new ETag(DateTimeOffset.Now.Ticks.ToString()).ToString("H");

        lock (EntityLock(entityPath))
        {
            File.WriteAllText(entityPath, root.ToJsonString());
        }
    }

    internal void UpdateEntity(Stream input, SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string tableName, string storageAccountName, string partitionKey,
                               string rowKey, IHeaderDictionary headers, bool merge = false)
    {
        logger.LogDebug(nameof(TableServiceDataPlane), nameof(UpdateEntity), "Executing {0}: {1} {2}", nameof(UpdateEntity), tableName, storageAccountName);

        PathGuard.ValidateName(partitionKey);
        PathGuard.ValidateName(rowKey);

        // Absent If-Match means an unconditional update (e.g. InsertOrMerge /
        // InsertOrReplace, which send no precondition). Default to "*" so the
        // etag check below is skipped, mirroring DeleteEntity. Without this an
        // unconditional upsert against an *existing* entity wrongly 412s.
        var etag = headers["If-Match"].FirstOrDefault() ?? "*";
        var path = resourceProvider.GetTableDataPath(subscriptionIdentifier, resourceGroupIdentifier, tableName, storageAccountName);

        using var sr = new StreamReader(input, leaveOpen: true);

        var rawContent = sr.ReadToEnd();

        var fileName = $"{PathGuard.SanitizeName(partitionKey)}_{PathGuard.SanitizeName(rowKey)}.json";
        var entityPath = Path.Combine(path, fileName);
        PathGuard.EnsureWithinDirectory(entityPath, path);

        if(File.Exists(entityPath) == false)
        {
            // Not existing entry — for upsert callers, the stream is left open so they can retry via UpsertEntity.
            logger.LogDebug(nameof(TableServiceDataPlane), nameof(UpdateEntity), "Executing {0}: Not existing entry.", nameof(UpdateEntity));
            throw new EntityNotFoundException();
        }

        // The whole read-modify-write runs under the per-entity lock so a reader never sees the file
        // mid-rewrite (empty/partial JSON) and two conditional updates cannot both pass the etag check
        // before either writes (the second now correctly 412s). Those races surfaced as intermittent
        // 500s when a high-frequency writer (a fast job poll) overlapped a reader of the same entity.
        lock (EntityLock(entityPath))
        {
            if (File.Exists(entityPath) == false)
            {
                logger.LogDebug(nameof(TableServiceDataPlane), nameof(UpdateEntity), "Executing {0}: Not existing entry.", nameof(UpdateEntity));
                throw new EntityNotFoundException();
            }

            var existingJson = File.ReadAllText(entityPath);

            if (etag != "*")
            {
                var node = JsonNode.Parse(existingJson);
                if (!EtagMatches(etag, node?["odata.etag"]?.GetValue<string>(), node?["Timestamp"]?.GetValue<string>()))
                    throw new UpdateConditionNotSatisfiedException();
            }

            var root = JsonNode.Parse(rawContent)!.AsObject();

            // Merge Entity (MERGE / PATCH / InsertOrMerge) overlays the request's properties
            // onto the stored entity and PRESERVES stored properties the request omitted.
            // A plain replace (the Update/Replace and InsertOrReplace path) drops every
            // property absent from a partial merge body, silently nulling fields the caller
            // never intended to clear.
            if (merge && JsonNode.Parse(existingJson) is JsonObject existing)
            {
                foreach (var property in root)
                    existing[property.Key] = property.Value?.DeepClone();
                root = existing;
            }

            var newEtag = new ETag(DateTimeOffset.Now.Ticks.ToString());
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff'Z'");

            // The entity keys are authoritative from the request URL; an update/merge body
            // may omit them (they are not required in the payload). Persist them so the
            // stored entity always carries PartitionKey/RowKey - otherwise a later query
            // returns a keyless entity and SDK entity resolvers dereference a null RowKey.
            root["PartitionKey"] = partitionKey;
            root["RowKey"] = rowKey;
            root["Timestamp"] = timestamp;
            root["odata.etag"] = newEtag.ToString("H");

            File.WriteAllText(entityPath, root.ToJsonString());
        }
    }

    // Per-entity-file lock. The data plane is file-backed and is instantiated per request, so a STATIC,
    // entity-path-keyed lock is what actually serialises concurrent access to a given entity across all
    // requests in this (single) process. Held across each read-modify-write (and across reads), it stops
    // a reader from observing a file mid-rewrite or in a delete-then-write gap - the torn/missing reads
    // that surfaced as intermittent 500s under a fast writer overlapping a reader of the same entity -
    // and gives conditional updates real read-check-write atomicity. Lock objects are keyed by the
    // normalised full path; the set is bounded by the number of distinct entities touched.
    private static readonly ConcurrentDictionary<string, object> EntityLocks = new(StringComparer.Ordinal);

    private static object EntityLock(string entityPath)
        => EntityLocks.GetOrAdd(Path.GetFullPath(entityPath), static _ => new object());
}
