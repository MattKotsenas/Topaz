using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Service.Storage.Endpoints.Table;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Regression for the Table $batch entity-key encoding bug.
///
/// A batched MERGE/PUT/DELETE addresses an entity by its URL, e.g.
/// <c>MyTable(PartitionKey='p1',RowKey='region%3Awestus')</c>. Azure SDKs URL-encode reserved
/// characters in that URL (':' becomes %3A). A plain insert, by contrast, carries the key un-encoded in
/// the request body. If the batch path does NOT URL-decode the URL key (as the non-batch entity path
/// already does in <c>TableDataPlaneEndpointBase.GetOperationDataForUpdateOperation</c>), the encoded
/// and raw forms persist as TWO distinct physical rows for one logical entity. A later partition query
/// then returns both, which breaks SDK consumers that key results by a single property (e.g. building a
/// dictionary keyed by an entity property hits a duplicate key).
/// </summary>
[TestFixture]
public class TableBatchKeyEncodingTests
{
    private static IHeaderDictionary Unconditional() => new HeaderDictionary { ["If-Match"] = "*" };

    private static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// A RowKey containing a reserved ':' is URL-encoded as %3A inside the entity URL. The batch path
    /// must decode it back to the key an insert stored from the (already-decoded) request body. This
    /// assertion is filesystem-free, so it exercises the real ':' character portably (on Windows ':' is
    /// the volume separator, so the file-backed test below uses a filename-safe character instead - the
    /// decode mechanism under test is identical for any encoded character).
    /// </summary>
    [Test]
    public void ParsePath_UrlEncodedRowKey_IsDecodedToTheRawKey()
    {
        const string rawRowKey = "region:westus2:stamp-3";
        var encoded = Uri.EscapeDataString(rawRowKey);
        var url = $"Deployments(PartitionKey='p1',RowKey='{encoded}')";

        // Precondition: the URL really is encoded (so decoding does real work and the test is non-vacuous).
        Assert.That(encoded, Does.Contain("%3A"), "precondition: ':' must be URL-encoded as %3A in the entity URL");
        Assert.That(encoded, Is.Not.EqualTo(rawRowKey), "precondition: the encoded key differs from the raw key");

        var (table, pk, rk) = BatchEndpoint.ParsePath(url);

        Assert.That(table, Is.EqualTo("Deployments"));
        Assert.That(pk, Is.EqualTo("p1"));
        Assert.That(rk, Is.EqualTo(rawRowKey),
            "the batch path must URL-decode the RowKey so a batched MERGE/PUT targets the same entity an insert stored from its body");
    }

    /// <summary>
    /// End-to-end at the data layer: an insert (raw key in the body) followed by a batched MERGE
    /// addressing the SAME entity by its URL-encoded key must update the one row, not create a second.
    /// Uses '@' (encoded %40) rather than ':' so the entity filename is valid on the Windows dev box as
    /// well as the Linux container; the decode behaviour being verified is identical for any reserved
    /// character.
    /// </summary>
    [Test]
    public void BatchedMergeWithUrlEncodedKey_UpdatesTheSameRow_NoDuplicate()
    {
        var logger = new PrettyTopazLogger();
        var provider = new TableResourceProvider(logger);
        var dataPlane = new TableServiceDataPlane(provider, logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string table = "Widgets";
        const string pk = "p1";
        // A reserved char ('@') the SDK URL-encodes (%40) but that is a valid filename character on every OS.
        const string rawRowKey = "owner@example-1";

        var dataDir = provider.GetTableDataPath(subscription, resourceGroup, table, account);
        Directory.CreateDirectory(dataDir);

        try
        {
            // 1) Insert as a POST does: the key is un-encoded in the body.
            dataPlane.InsertEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rawRowKey}}","State":"Enabled","Label":"first"}"""),
                subscription, resourceGroup, table, account);

            // Precondition: exactly one physical row after the insert.
            Assert.That(Directory.GetFiles(dataDir, "*.json").Length, Is.EqualTo(1),
                "precondition: the insert created exactly one row");

            // 2) A batched MERGE addresses the SAME entity by URL, where the SDK URL-encodes '@' as %40.
            var url = $"{table}(PartitionKey='{pk}',RowKey='{Uri.EscapeDataString(rawRowKey)}')";
            Assert.That(url, Does.Contain("%40"), "precondition: '@' is URL-encoded in the batch sub-operation URL");

            var (_, parsedPk, parsedRk) = BatchEndpoint.ParsePath(url);
            Assert.That(parsedRk, Is.EqualTo(rawRowKey), "the parsed RowKey must be decoded back to the raw insert key");

            // Dispatch the merge exactly as the batch executor does: UpdateEntity with the parsed keys.
            dataPlane.UpdateEntity(
                Body("""{"State":"Completed"}"""),
                subscription, resourceGroup, table, account, parsedPk!, parsedRk!, Unconditional(), merge: true);

            // 3) The fix: still exactly ONE row - the merge updated the existing entity rather than
            //    creating a second (encoded-key) row. Without the decode this is 2 (the duplicate).
            Assert.That(Directory.GetFiles(dataDir, "*.json").Length, Is.EqualTo(1),
                "a batched merge addressing the entity by its URL-encoded key must update the SAME row, not create a duplicate");

            // 4) The merge actually applied to the original entity and preserved the omitted property.
            var merged = JsonNode.Parse(dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rawRowKey))!.AsObject();
            Assert.That((string?)merged["State"], Is.EqualTo("Completed"), "merge applied to the existing entity");
            Assert.That((string?)merged["Label"], Is.EqualTo("first"), "merge preserved the omitted Label");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
