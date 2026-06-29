using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Topaz.EventPipeline;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage.Exceptions;
using Topaz.Service.Storage.Persistence;
using Topaz.Shared;

namespace Topaz.Service.Storage.Endpoints.Table;

/// <summary>
/// OData entity group transaction ($batch) endpoint for Table storage.
///
/// Azure Table storage exposes transactional writes via
/// POST {account}.table.../$batch with a multipart/mixed body containing a
/// single change set of insert/update/merge/delete operations
/// (https://learn.microsoft.com/rest/api/storageservices/performing-entity-group-transactions).
/// Both the legacy Microsoft.Azure.Storage / Cosmos.Table SDK and the modern
/// Azure.Data.Tables SDK (SubmitTransaction) use it.
///
/// The change set is parsed and each sub-operation is dispatched to the existing
/// <see cref="TableServiceDataPlane"/> (Insert / Update-with-InsertOrMerge-
/// fallback / Upsert / Delete) so the writes persist; the multipart/mixed
/// response is then assembled with a per-operation status. Only the
/// single-change-set subset that Azure documents is supported.
///
/// Registered before the wildcard POST /{tableName} so /$batch matches here.
/// </summary>
internal sealed class BatchEndpoint(Pipeline eventPipeline, ITopazLogger logger)
    : TableDataPlaneEndpointBase(eventPipeline, logger), IEndpointDefinition
{
    private static readonly Regex RequestLineRegex = new(
        @"^(?<method>GET|POST|PUT|MERGE|PATCH|DELETE)\s+(?<url>\S+)\s+HTTP/\d\.\d\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex EntityKeyRegex = new(
        @"\(PartitionKey='(?<pk>[^']*)',\s*RowKey='(?<rk>[^']*)'\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContentIdRegex = new(
        @"Content-ID:\s*(?<id>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PreferRegex = new(
        @"Prefer:\s*(?<pref>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IfMatchRegex = new(
        @"If-Match:\s*(?<etag>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Serializes $batch changesets per (account, partition) so concurrent Entity Group Transactions on the same
    // partition do not interleave - matching real Azure Table EGT isolation. Keyed by account + partition key.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> BatchPartitionLocks =
        new(StringComparer.Ordinal);

    private static object BatchPartitionLock(string account, string? partitionKey) =>
        BatchPartitionLocks.GetOrAdd(account + "/" + (partitionKey ?? string.Empty), static _ => new object());

    public string? ProviderNamespace => "Microsoft.Storage";

    // '^'-prefixed segment => Router treats it as a regex. Matches exactly "/$batch".
    public string[] Endpoints => ["POST /^\\$batch$"];

    public string[] Permissions => ["Microsoft.Storage/storageAccounts/tableServices/tables/entities/write"];

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        if (RejectIfSecondaryHostForMutation(context.Request.Headers, response)) return;
        if (!TryGetStorageAccount(context.Request.Headers, out var storageAccount))
        {
            response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        var subscriptionIdentifier = storageAccount!.GetSubscription();
        var resourceGroupIdentifier = storageAccount!.GetResourceGroup();

        if (!IsRequestAuthorized(subscriptionIdentifier, resourceGroupIdentifier, storageAccount.Name, context, response))
        {
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = reader.ReadToEnd();
        }

        var operations = ParseChangeSetOperations(body);
        Logger.LogDebug(nameof(BatchEndpoint), nameof(GetResponse),
            "Dispatching {0} $batch sub-operation(s) on '{1}'.", operations.Count.ToString(), storageAccount.Name);

        var actions = new List<TableBatchAction>(operations.Count);
        foreach (var op in operations)
            actions.Add(ToAction(op, subscriptionIdentifier, resourceGroupIdentifier, storageAccount.Name));

        // Azure $batch is an Entity Group Transaction: atomic (all-or-nothing) AND serializable within the single
        // partition it targets. Execute the whole changeset in one store transaction under a per-(account,partition)
        // lock, so two concurrent EGTs neither interleave nor partially apply - the isolation + atomicity a
        // distributed job sequencer relies on for exactly-once successor scheduling. (All EGT ops share a partition.)
        var batchPartitionKey = operations.Count > 0 ? operations[0].PartitionKey : null;
        IReadOnlyList<TableBatchResult> results = Array.Empty<TableBatchResult>();
        TableBatchConflictException? conflict = null;
        lock (BatchPartitionLock(storageAccount.Name, batchPartitionKey))
        {
            try
            {
                results = DataPlane.ExecuteBatch(actions);
            }
            catch (TableBatchConflictException ex)
            {
                conflict = ex;
            }
        }

        var changesetBoundary = "changesetresponse_" + Guid.NewGuid().ToString("N");
        var batchBoundary = "batchresponse_" + Guid.NewGuid().ToString("N");

        var sb = new StringBuilder();
        sb.Append("--").Append(batchBoundary).Append("\r\n");
        sb.Append("Content-Type: multipart/mixed; boundary=").Append(changesetBoundary).Append("\r\n\r\n");

        if (conflict != null)
        {
            // The EGT rolled back: Azure returns a single changeset error keyed to the failing operation, and the
            // SDK surfaces it as a transaction failure carrying that operation's index. Nothing else was applied.
            var failedOp = operations[conflict.Index];
            var failure = MapFailure(conflict.InnerException);
            AppendErrorPart(sb, changesetBoundary, failedOp, failure);
            Logger.LogDebug(nameof(BatchEndpoint), nameof(GetResponse),
                "$batch rolled back at op {0} ({1}) -> {2}", conflict.Index.ToString(), failedOp.Method,
                ((int)failure.status).ToString());
        }
        else
        {
            for (var i = 0; i < operations.Count; i++)
                AppendSuccessPart(sb, changesetBoundary, operations[i], results[i]);
        }

        sb.Append("--").Append(changesetBoundary).Append("--\r\n");
        sb.Append("--").Append(batchBoundary).Append("--\r\n");

        response.StatusCode = HttpStatusCode.Accepted;
        response.Content = new StringContent(sb.ToString(), Encoding.UTF8);
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation(
            "Content-Type", "multipart/mixed; boundary=" + batchBoundary);
    }

    // Maps a parsed sub-operation to an atomic store action. POST (insert) takes its keys from the entity body;
    // every other verb from the entity URL. An absent If-Match is an unconditional write: for MERGE/PUT that is the
    // SDK's InsertOrMerge / InsertOrReplace (insert when the row is missing). A present If-Match is a conditional
    // update - 404 when the row is missing, 412 on an etag mismatch - which is faithful Azure EGT precondition
    // semantics (the optimistic concurrency a concurrent writer can lose).
    private TableBatchAction ToAction(BatchOperation op, SubscriptionIdentifier subscription,
        ResourceGroupIdentifier resourceGroup, string account)
    {
        var scope = DataPlane.GetTableScope(subscription, resourceGroup, op.TableName, account);
        var hasIfMatch = !string.IsNullOrEmpty(op.IfMatch);
        var ifMatch = hasIfMatch ? op.IfMatch! : "*";
        switch (op.Method.ToUpperInvariant())
        {
            case "GET":
                return new TableBatchAction(TableBatchOp.Retrieve, scope, op.PartitionKey!, op.RowKey!, null, ifMatch, false);

            case "POST":
                var (pk, rk) = KeysFromBody(op.Body);
                return new TableBatchAction(TableBatchOp.Insert, scope, pk, rk, op.Body, ifMatch, false);

            case "PUT":
                return new TableBatchAction(TableBatchOp.Replace, scope, op.PartitionKey!, op.RowKey!, op.Body, ifMatch, !hasIfMatch);

            case "MERGE":
            case "PATCH":
                return new TableBatchAction(TableBatchOp.Merge, scope, op.PartitionKey!, op.RowKey!, op.Body, ifMatch, !hasIfMatch);

            case "DELETE":
                return new TableBatchAction(TableBatchOp.Delete, scope, op.PartitionKey!, op.RowKey!, null, ifMatch, false);

            default:
                // Unreachable: the request-line regex only admits the verbs above.
                throw new InvalidOperationException("Unsupported $batch method " + op.Method);
        }
    }

    // A keyless insert (POST) carries its PartitionKey/RowKey in the entity body, not the URL.
    private static (string pk, string rk) KeysFromBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return (string.Empty, string.Empty);
        try
        {
            var node = JsonNode.Parse(body);
            return ((string?)node?["PartitionKey"] ?? string.Empty, (string?)node?["RowKey"] ?? string.Empty);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    // Appends one successful sub-operation response part. A retrieve echoes the row (200); an insert echoes the
    // stored entity (201) unless the request preferred no content (204); merge/replace/delete return 204. The
    // stored body (with the server Timestamp + odata.etag) comes straight from the transactional batch result.
    private void AppendSuccessPart(StringBuilder sb, string changesetBoundary, BatchOperation op, TableBatchResult result)
    {
        sb.Append("--").Append(changesetBoundary).Append("\r\n");
        sb.Append("Content-Type: application/http\r\n");
        sb.Append("Content-Transfer-Encoding: binary\r\n\r\n");

        switch (op.Method.ToUpperInvariant())
        {
            case "GET":
                sb.Append("HTTP/1.1 200 OK\r\n");
                sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
                sb.Append("Content-Type: application/json;odata=minimalmetadata;streaming=true;charset=utf-8\r\n");
                sb.Append("DataServiceVersion: 3.0;\r\n\r\n");
                sb.Append(result.Body).Append("\r\n");
                break;

            case "POST":
                var insertEtag = EtagHeaderFromEntity(result.Body);
                if (PrefersNoContent(op.Prefer))
                {
                    sb.Append("HTTP/1.1 204 No Content\r\n");
                    sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
                    if (insertEtag != null) sb.Append("ETag: ").Append(insertEtag).Append("\r\n");
                    sb.Append("DataServiceVersion: 3.0;\r\n\r\n");
                }
                else
                {
                    sb.Append("HTTP/1.1 201 Created\r\n");
                    sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
                    if (insertEtag != null) sb.Append("ETag: ").Append(insertEtag).Append("\r\n");
                    sb.Append("Content-Type: application/json;odata=minimalmetadata;streaming=true;charset=utf-8\r\n");
                    sb.Append("DataServiceVersion: 3.0;\r\n\r\n");
                    sb.Append(result.Body).Append("\r\n");
                }
                break;

            default: // PUT / MERGE / PATCH / DELETE
                sb.Append("HTTP/1.1 204 No Content\r\n");
                sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
                sb.Append("DataServiceVersion: 3.0;\r\n\r\n");
                break;
        }
    }

    // Emits the single changeset error part Azure returns for a rolled-back EGT, keyed to the failing op's Content-ID.
    private static void AppendErrorPart(StringBuilder sb, string changesetBoundary, BatchOperation op,
        (HttpStatusCode status, string code) failure)
    {
        sb.Append("--").Append(changesetBoundary).Append("\r\n");
        sb.Append("Content-Type: application/http\r\n");
        sb.Append("Content-Transfer-Encoding: binary\r\n\r\n");
        sb.Append("HTTP/1.1 ").Append((int)failure.status).Append(' ').Append(ReasonPhrase(failure.status)).Append("\r\n");
        sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
        sb.Append("Content-Type: application/json\r\n\r\n");
        sb.Append("{\"odata.error\":{\"code\":\"").Append(failure.code)
          .Append("\",\"message\":{\"lang\":\"en-US\",\"value\":\"").Append(failure.code).Append("\"}}}").Append("\r\n");
    }

    private static (HttpStatusCode status, string code) MapFailure(Exception? cause) => cause switch
    {
        EntityAlreadyExistsException => (HttpStatusCode.Conflict, "EntityAlreadyExists"),
        EntityNotFoundException => (HttpStatusCode.NotFound, "ResourceNotFound"),
        UpdateConditionNotSatisfiedException => (HttpStatusCode.PreconditionFailed, "UpdateConditionNotSatisfied"),
        _ => (HttpStatusCode.BadRequest, "InvalidInput"),
    };

    private static bool PrefersNoContent(string? prefer) =>
        prefer != null && prefer.Contains("return-no-content", StringComparison.OrdinalIgnoreCase);

    // Builds the sub-response ETag header the legacy SDK parses for an insert
    // (W/"datetime'<url-encoded-timestamp>'"); the SDK derives the entity Timestamp from it.
    private static string? EtagHeaderFromEntity(string? entityJson)
    {
        if (string.IsNullOrEmpty(entityJson)) return null;
        try
        {
            var ts = (string?)JsonNode.Parse(entityJson)?["Timestamp"];
            return ts == null ? null : "W/\"datetime'" + Uri.EscapeDataString(ts) + "'\"";
        }
        catch
        {
            return null;
        }
    }

    // Parses the single OData change set into its operations. Tolerant of the exact
    // boundary strings (derived from the body) and CRLF/LF line endings.
    private static List<BatchOperation> ParseChangeSetOperations(string body)
    {
        var operations = new List<BatchOperation>();

        // Each operation is an "application/http" part carrying an embedded HTTP request.
        // Anchor on the request lines, then slice each operation's headers + body.
        var matches = RequestLineRegex.Matches(body);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var method = m.Groups["method"].Value;
            var url = m.Groups["url"].Value;

            var contentStart = m.Index + m.Length;
            var contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var segment = body.Substring(contentStart, contentEnd - contentStart);

            // Embedded request headers end at the first blank line; the entity body follows.
            string? entityBody = null;
            var blank = FirstBlankLine(segment);
            if (blank >= 0)
            {
                entityBody = StripTrailingBoundary(segment.Substring(blank).TrimStart('\r', '\n'));
            }

            var (tableName, pk, rk) = ParsePath(url);
            if (string.IsNullOrEmpty(tableName)) continue;

            // Content-ID can appear in the part headers (before the request line) - search
            // the preceding slice too. Cheap: search the whole operation neighbourhood.
            var contentIdMatch = ContentIdRegex.Match(
                body.Substring(Math.Max(0, m.Index - 200), Math.Min(body.Length, m.Index + 1) - Math.Max(0, m.Index - 200)));

            // The Prefer header (in the embedded request headers, before the body) controls
            // whether an insert echoes its entity: "return-no-content" -> 204, else 201+body.
            var headerSlice = blank >= 0 ? segment.Substring(0, blank) : segment;
            var preferMatch = PreferRegex.Match(headerSlice);

            // Per-operation If-Match precondition (Azure EGT honours it per sub-request). Absent => unconditional.
            var ifMatchMatch = IfMatchRegex.Match(headerSlice);

            operations.Add(new BatchOperation
            {
                Method = method,
                TableName = tableName,
                PartitionKey = pk,
                RowKey = rk,
                Body = entityBody,
                ContentId = contentIdMatch.Success ? contentIdMatch.Groups["id"].Value : null,
                Prefer = preferMatch.Success ? preferMatch.Groups["pref"].Value : null,
                IfMatch = ifMatchMatch.Success ? ifMatchMatch.Groups["etag"].Value.Trim() : null,
            });
        }

        return operations;
    }

    // internal (not private) so the regression test can pin the URL-decode of entity keys directly.
    internal static (string tableName, string? pk, string? rk) ParsePath(string url)
    {
        // url may be absolute (https://acct.table.host/Table(...)) or a path. Reduce
        // to the path after the host/root.
        string path;
        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var afterScheme = url.Substring(schemeIdx + 3);
            var slash = afterScheme.IndexOf('/');
            path = slash >= 0 ? afterScheme.Substring(slash + 1) : string.Empty;
        }
        else
        {
            path = url.TrimStart('/');
        }

        var q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);

        var keyMatch = EntityKeyRegex.Match(path);
        if (keyMatch.Success)
        {
            var tableName = path.Substring(0, keyMatch.Index);
            // Keys arrive URL-encoded in the batch sub-operation URL (e.g. ':' as %3A), exactly as in
            // the non-batch entity path. Decode them so a batched MERGE/PUT/DELETE targets the same
            // physical entity an insert stored from the (already-decoded) request body. Without this the
            // encoded and decoded key forms diverge into two rows for one logical entity, which a later
            // partition query then returns twice - breaking SDK consumers that key query results by a
            // single entity property (e.g. building a dictionary keyed by a property hits a duplicate
            // key). Mirrors TableDataPlaneEndpointBase.GetOperationDataForUpdateOperation.
            var pk = Uri.UnescapeDataString(keyMatch.Groups["pk"].Value);
            var rk = Uri.UnescapeDataString(keyMatch.Groups["rk"].Value);
            return (tableName, pk, rk);
        }

        // A keyless insert (POST) targets "Table()" (empty parens) - strip them to
        // recover the table name; otherwise the insert hits a non-existent "Table()".
        if (path.EndsWith("()", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 2);
        return (path, null, null);
    }

    private static int FirstBlankLine(string s)
    {
        var idx = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (idx >= 0) return idx;
        return s.IndexOf("\n\n", StringComparison.Ordinal);
    }

    private static string StripTrailingBoundary(string body)
    {
        // Remove a trailing "--boundary" / "--boundary--" line and surrounding whitespace.
        var idx = body.IndexOf("\r\n--", StringComparison.Ordinal);
        if (idx < 0) idx = body.IndexOf("\n--", StringComparison.Ordinal);
        if (idx >= 0) body = body.Substring(0, idx);
        return body.Trim('\r', '\n', ' ');
    }

    private static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NoContent => "No Content",
        HttpStatusCode.Created => "Created",
        HttpStatusCode.Accepted => "Accepted",
        HttpStatusCode.Conflict => "Conflict",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.PreconditionFailed => "Precondition Failed",
        HttpStatusCode.BadRequest => "Bad Request",
        _ => status.ToString(),
    };

    private sealed class BatchOperation
    {
        public string Method { get; init; } = "POST";
        public string TableName { get; init; } = string.Empty;
        public string? PartitionKey { get; init; }
        public string? RowKey { get; init; }
        public string? Body { get; init; }
        public string? ContentId { get; init; }
        public string? Prefer { get; init; }
        public string? IfMatch { get; init; }
    }
}
