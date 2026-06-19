using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Topaz.EventPipeline;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage.Exceptions;
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

        if (!IsRequestAuthorized(subscriptionIdentifier, resourceGroupIdentifier, storageAccount.Name, context))
        {
            response.StatusCode = HttpStatusCode.Unauthorized;
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = reader.ReadToEnd();
        }

        // The SDK's transactional ops are unconditional InsertOrMerge / InsertOrReplace.
        // UpdateEntity only does an unconditional update when If-Match is "*"; force it
        // so re-writes of existing entities merge instead of failing a concurrency check.
        // (New entities throw EntityNotFound first and take the insert fallback.)
        context.Request.Headers["If-Match"] = "*";

        var operations = ParseChangeSetOperations(body);
        Logger.LogDebug(nameof(BatchEndpoint), nameof(GetResponse),
            "Dispatching {0} $batch sub-operation(s) on '{1}'.", operations.Count.ToString(), storageAccount.Name);

        var changesetBoundary = "changesetresponse_" + Guid.NewGuid().ToString("N");
        var batchBoundary = "batchresponse_" + Guid.NewGuid().ToString("N");

        var sb = new StringBuilder();
        sb.Append("--").Append(batchBoundary).Append("\r\n");
        sb.Append("Content-Type: multipart/mixed; boundary=").Append(changesetBoundary).Append("\r\n\r\n");

        foreach (var op in operations)
        {
            var (status, errorBody, echoBody) = ExecuteOperation(
                op, subscriptionIdentifier, resourceGroupIdentifier, storageAccount.Name, context.Request.Headers);
            Logger.LogDebug(nameof(BatchEndpoint), nameof(GetResponse), "$batch op {0} {1}({2},{3}) -> {4}",
                op.Method, op.TableName, op.PartitionKey ?? string.Empty, op.RowKey ?? string.Empty, ((int)status).ToString());

            sb.Append("--").Append(changesetBoundary).Append("\r\n");
            sb.Append("Content-Type: application/http\r\n");
            sb.Append("Content-Transfer-Encoding: binary\r\n\r\n");
            sb.Append("HTTP/1.1 ").Append((int)status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
            if (errorBody != null)
            {
                sb.Append("Content-Type: application/json\r\n\r\n");
                sb.Append(errorBody).Append("\r\n");
            }
            else if (echoBody != null)
            {
                // Operation with an entity body to echo (e.g. a retrieve returning the row).
                sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
                sb.Append("Content-Type: application/json;odata=minimalmetadata;streaming=true;charset=utf-8\r\n");
                sb.Append("DataServiceVersion: 3.0;\r\n\r\n");
                sb.Append(echoBody).Append("\r\n");
            }
            else
            {
                sb.Append("Content-ID: ").Append(op.ContentId ?? "0").Append("\r\n");
                sb.Append("DataServiceVersion: 3.0;\r\n\r\n");
            }
        }

        sb.Append("--").Append(changesetBoundary).Append("--\r\n");
        sb.Append("--").Append(batchBoundary).Append("--\r\n");

        response.StatusCode = HttpStatusCode.Accepted;
        response.Content = new StringContent(sb.ToString(), Encoding.UTF8);
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation(
            "Content-Type", "multipart/mixed; boundary=" + batchBoundary);
    }

    private (HttpStatusCode status, string? errorBody, string? echoBody) ExecuteOperation(
        BatchOperation op, SubscriptionIdentifier subscription, ResourceGroupIdentifier resourceGroup,
        string account, IHeaderDictionary headers)
    {
        try
        {
            var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(op.Body ?? string.Empty));
            switch (op.Method.ToUpperInvariant())
            {
                case "GET":
                    // Retrieve within a change set: echo the entity (200). A missing entity
                    // throws below and yields 404, which the client tolerates for a retrieve.
                    return (HttpStatusCode.OK, null,
                        DataPlane.GetEntity(subscription, resourceGroup, op.TableName, account, op.PartitionKey!, op.RowKey!));

                case "POST":
                    // Insert into the table named by the path (no entity key).
                    // TableOperation.Insert defaults to echoContent=false, so the client
                    // expects 204 No Content (not 201).
                    DataPlane.InsertEntity(bodyStream, subscription, resourceGroup, op.TableName, account);
                    return (HttpStatusCode.NoContent, null, null);

                case "PUT":
                case "MERGE":
                case "PATCH":
                    // Update; on a missing entity fall back to insert (InsertOrMerge /
                    // InsertOrReplace semantics the SDK uses for config writes).
                    try
                    {
                        DataPlane.UpdateEntity(bodyStream, subscription, resourceGroup, op.TableName, account,
                            op.PartitionKey!, op.RowKey!, headers);
                    }
                    catch (EntityNotFoundException)
                    {
                        var retry = new MemoryStream(Encoding.UTF8.GetBytes(op.Body ?? string.Empty));
                        DataPlane.UpsertEntity(retry, subscription, resourceGroup, op.TableName, account,
                            op.PartitionKey!, op.RowKey!);
                    }

                    return (HttpStatusCode.NoContent, null, null);

                case "DELETE":
                    DataPlane.DeleteEntity(subscription, resourceGroup, op.TableName, account,
                        op.PartitionKey!, op.RowKey!, headers);
                    return (HttpStatusCode.NoContent, null, null);

                default:
                    return (HttpStatusCode.BadRequest, "{\"odata.error\":{\"code\":\"InvalidInput\"}}", null);
            }
        }
        catch (EntityAlreadyExistsException)
        {
            return (HttpStatusCode.Conflict, "{\"odata.error\":{\"code\":\"EntityAlreadyExists\"}}", null);
        }
        catch (EntityNotFoundException)
        {
            return (HttpStatusCode.NotFound, "{\"odata.error\":{\"code\":\"ResourceNotFound\"}}", null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return (HttpStatusCode.BadRequest, "{\"odata.error\":{\"code\":\"InvalidInput\"}}", null);
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

            operations.Add(new BatchOperation
            {
                Method = method,
                TableName = tableName,
                PartitionKey = pk,
                RowKey = rk,
                Body = entityBody,
                ContentId = contentIdMatch.Success ? contentIdMatch.Groups["id"].Value : null,
            });
        }

        return operations;
    }

    private static (string tableName, string? pk, string? rk) ParsePath(string url)
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
            return (tableName, keyMatch.Groups["pk"].Value, keyMatch.Groups["rk"].Value);
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
    }
}
