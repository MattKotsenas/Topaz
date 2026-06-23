using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Direct, host-free coverage of the table data-plane update semantics: MERGE
/// (InsertOrMerge / Merge Entity) must overlay the request's properties onto the
/// stored entity and PRESERVE properties the request body omits, while a REPLACE
/// (Update / InsertOrReplace) drops them. The batch ($batch change set) executor
/// selects the same merge flag per sub-operation (MERGE/PATCH -> merge, PUT ->
/// replace), so this pins the behavior both the single-entity and batch paths rely
/// on without needing a running host.
/// </summary>
[TestFixture]
public class TableMergeSemanticsTests
{
    private static IHeaderDictionary Unconditional()
        => new HeaderDictionary { ["If-Match"] = "*" };

    private static System.IO.Stream Body(string json)
        => new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));

    [Test]
    public void Merge_PreservesOmittedProperties_ReplaceDropsThem()
    {
        var logger = new PrettyTopazLogger();
        var provider = new TableResourceProvider(logger);
        var dataPlane = new TableServiceDataPlane(provider, logger);

        // Isolated per-run location under {cwd}/.topaz/... so the test never
        // collides with other fixtures or a previous run.
        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string table = "MergeTable";
        const string pk = "pk1";
        const string rk = "rk1";

        var dataDir = provider.GetTableDataPath(subscription, resourceGroup, table, account);
        Directory.CreateDirectory(dataDir);

        try
        {
            // Seed an entity with three data properties.
            dataPlane.InsertEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Alpha":"1","Beta":"2","Gamma":"3"}"""),
                subscription, resourceGroup, table, account);

            // Precondition: all three are stored.
            var seeded = JsonNode.Parse(dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rk))!.AsObject();
            Assert.That((string?)seeded["Alpha"], Is.EqualTo("1"), "precondition: Alpha seeded");
            Assert.That((string?)seeded["Gamma"], Is.EqualTo("3"), "precondition: Gamma seeded");

            // MERGE: change Beta, add Delta, OMIT Alpha and Gamma.
            dataPlane.UpdateEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Beta":"22","Delta":"4"}"""),
                subscription, resourceGroup, table, account, pk, rk, Unconditional(), merge: true);

            var merged = JsonNode.Parse(dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rk))!.AsObject();
            Assert.That((string?)merged["Alpha"], Is.EqualTo("1"), "merge must PRESERVE the omitted Alpha");
            Assert.That((string?)merged["Gamma"], Is.EqualTo("3"), "merge must PRESERVE the omitted Gamma");
            Assert.That((string?)merged["Beta"], Is.EqualTo("22"), "merge must apply the updated Beta");
            Assert.That((string?)merged["Delta"], Is.EqualTo("4"), "merge must add the new Delta");

            // REPLACE: send only Zeta; every other data property must disappear.
            dataPlane.UpdateEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Zeta":"9"}"""),
                subscription, resourceGroup, table, account, pk, rk, Unconditional(), merge: false);

            var replaced = JsonNode.Parse(dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rk))!.AsObject();
            Assert.That((string?)replaced["Zeta"], Is.EqualTo("9"), "replace must apply the new Zeta");
            Assert.That(replaced.ContainsKey("Alpha"), Is.False, "replace must DROP the omitted Alpha");
            Assert.That(replaced.ContainsKey("Beta"), Is.False, "replace must DROP the omitted Beta");
            Assert.That(replaced.ContainsKey("Gamma"), Is.False, "replace must DROP the omitted Gamma");
            Assert.That(replaced.ContainsKey("Delta"), Is.False, "replace must DROP the omitted Delta");

            // Keys are always retained regardless of mode.
            Assert.That((string?)replaced["PartitionKey"], Is.EqualTo(pk));
            Assert.That((string?)replaced["RowKey"], Is.EqualTo(rk));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
