using FsCheck;
using FsCheck.Fluent;

using NUnit.Framework;

using Topaz.Service.Storage;

namespace Topaz.Tests;

/// <summary>
/// Property-based coverage for <see cref="BlobArtifactReader.TryBuildFetchRequest"/> - the pure request
/// builder behind resolving a deployment templateLink over the network. The invariants: the front door's
/// authority replaces the link's, the link's path + query (its SAS) survive byte-for-byte, and the link's
/// own account-bearing host is returned as the Host header so the front door routes to the right account.
/// </summary>
[TestFixture]
public class BlobArtifactReaderProperties
{
    private static readonly Gen<string> SchemeGen = Gen.Elements("http", "https");

    // Account-in-first-label storage hosts, like a client bakes into a templateLink SAS.
    private static readonly Gen<string> AccountHostGen = Gen.Elements(
        "azureemulator.blob.127.0.0.1.nip.io",
        "acct.blob.core.windows.net",
        "r4mocaccta.blob.storage.example.io");

    // Realistic, percent-encoded service-SAS queries: sig carries + / = as %2B %2F %3D. A single re-encode
    // corrupts the signature the backend validates - so preservation is the load-bearing property.
    private static readonly Gen<string> QueryGen = Gen.Elements(
        "?sv=2016-10-16&sr=b&sig=abc%2Bdef%2Fghi%3D&se=2026-06-23T12%3A00%3A00Z",
        "?sv=2025-07-05&ss=b&srt=o&sp=r&sig=Zm9v%2Bymx%2F%3D&se=2026-07-05T15%3A04%3A00Z",
        string.Empty);

    private static readonly Gen<string> PathGen = Gen.Elements(
        "/artifacts/StorageAccount.Template.json",
        "/system/a/b/c/nested.json",
        "/t.json");

    private static readonly Gen<string> FrontDoorHostGen = Gen.Elements(
        "host.docker.internal", "127.0.0.1", "localhost", "yarp.internal");

    private static readonly Gen<int> PortGen = Gen.Choose(1, 65535);

    private static Gen<(Uri Link, Uri FrontDoor)> CaseGen() =>
        from linkScheme in SchemeGen
        from host in AccountHostGen
        from path in PathGen
        from query in QueryGen
        from fdScheme in SchemeGen
        from fdHost in FrontDoorHostGen
        from fdPort in PortGen
        select (new Uri($"{linkScheme}://{host}{path}{query}"), new Uri($"{fdScheme}://{fdHost}:{fdPort}"));

    [Test]
    public void FetchRequest_preserves_path_and_query_byte_for_byte()
        => Prop.ForAll(CaseGen().ToArbitrary(), c =>
        {
            var ok = BlobArtifactReader.TryBuildFetchRequest(c.Link, c.FrontDoor, out var fetchUri, out _);
            return ok && fetchUri.PathAndQuery == c.Link.PathAndQuery;
        }).QuickCheckThrowOnFailure();

    [Test]
    public void FetchRequest_routes_to_the_front_door_authority()
        => Prop.ForAll(CaseGen().ToArbitrary(), c =>
        {
            var ok = BlobArtifactReader.TryBuildFetchRequest(c.Link, c.FrontDoor, out var fetchUri, out _);
            return ok
                && fetchUri.Scheme == c.FrontDoor.Scheme
                && fetchUri.Host == c.FrontDoor.Host
                && fetchUri.Port == c.FrontDoor.Port;
        }).QuickCheckThrowOnFailure();

    [Test]
    public void FetchRequest_returns_the_original_account_host_for_routing()
        => Prop.ForAll(CaseGen().ToArbitrary(), c =>
        {
            var ok = BlobArtifactReader.TryBuildFetchRequest(c.Link, c.FrontDoor, out _, out var hostHeader);
            return ok && hostHeader == c.Link.Host;
        }).QuickCheckThrowOnFailure();

    [Test]
    public void FetchRequest_rejects_a_relative_link()
    {
        var frontDoor = new Uri("http://host.docker.internal:8895");
        var ok = BlobArtifactReader.TryBuildFetchRequest(
            new Uri("/artifacts/t.json", UriKind.Relative), frontDoor, out _, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void FetchRequest_rejects_a_relative_front_door()
    {
        var link = new Uri("https://azureemulator.blob.127.0.0.1.nip.io/artifacts/t.json?sig=abc");
        var ok = BlobArtifactReader.TryBuildFetchRequest(link, new Uri("/x", UriKind.Relative), out _, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void FetchRequest_example_rewrites_authority_and_keeps_the_encoded_sas()
    {
        var link = new Uri(
            "https://azureemulator.blob.127.0.0.1.nip.io:443/artifacts/StorageAccount.Template.json"
            + "?sv=2016-10-16&sr=b&sig=fEfrdWROCB2Pnnj4bYs2IS6tQtgt%2F92gsBIVFG3ARZE%3D&sp=r");
        var frontDoor = new Uri("http://host.docker.internal:8895");

        var ok = BlobArtifactReader.TryBuildFetchRequest(link, frontDoor, out var fetchUri, out var hostHeader);

        Assert.That(ok, Is.True);
        Assert.That(
            fetchUri.ToString(),
            Is.EqualTo(
                "http://host.docker.internal:8895/artifacts/StorageAccount.Template.json"
                + "?sv=2016-10-16&sr=b&sig=fEfrdWROCB2Pnnj4bYs2IS6tQtgt%2F92gsBIVFG3ARZE%3D&sp=r"));
        Assert.That(hostHeader, Is.EqualTo("azureemulator.blob.127.0.0.1.nip.io"));
    }
}
