using System.Text.Json.Nodes;
using Topaz.Service.Storage.Exceptions;

namespace Topaz.Service.Storage.Persistence;

/// <summary>
/// Evaluates an If-Match precondition against a stored entity body. Two shapes occur: the stored odata.etag
/// (<c>"&lt;ticks&gt;"</c>, quote/weak-prefix insensitive) and the legacy Cosmos.Table SDK form derived from the
/// entity Timestamp (<c>W/"datetime'&lt;url-encoded-ts&gt;'"</c>). <c>*</c> matches unconditionally.
/// </summary>
internal static class EtagPrecondition
{
    public static void EnsureSatisfied(string ifMatch, string storedBody)
    {
        if (ifMatch == "*") return;
        var node = JsonNode.Parse(storedBody);
        if (!Matches(ifMatch, node?["odata.etag"]?.GetValue<string>(), node?["Timestamp"]?.GetValue<string>()))
            throw new UpdateConditionNotSatisfiedException();
    }

    public static bool Matches(string ifMatch, string? storedEtag, string? storedTimestamp)
    {
        if (string.IsNullOrEmpty(ifMatch)) return false;
        if (Normalize(storedEtag) == Normalize(ifMatch)) return true;

        const string marker = "datetime'";
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

    private static string Normalize(string? etag)
    {
        if (string.IsNullOrEmpty(etag)) return string.Empty;
        var e = etag.Trim();
        if (e.StartsWith("W/", StringComparison.Ordinal)) e = e.Substring(2);
        return e.Trim('"');
    }
}
