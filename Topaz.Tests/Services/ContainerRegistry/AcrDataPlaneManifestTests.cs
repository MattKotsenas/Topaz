using System.Security.Cryptography;
using System.Text;
using Topaz.Service.ContainerRegistry;
using Topaz.Service.Shared.Domain;
using Topaz.Shared;

namespace Topaz.Tests.Services.ContainerRegistry;

public sealed class AcrDataPlaneManifestTests : IDisposable
{
    private readonly SubscriptionIdentifier _subscription =
        SubscriptionIdentifier.From(Guid.NewGuid());
    private readonly ResourceGroupIdentifier _resourceGroup =
        ResourceGroupIdentifier.From("registry-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _registryName =
        "registry" + Guid.NewGuid().ToString("N")[..12];
    private readonly ContainerRegistryResourceProvider _provider;
    private readonly AcrDataPlane _dataPlane;

    public AcrDataPlaneManifestTests()
    {
        var logger = new PrettyTopazLogger();
        _provider = new ContainerRegistryResourceProvider(logger);
        _dataPlane = new AcrDataPlane(_provider, logger);
    }

    [Test]
    public void Manifest_pushed_by_digest_can_be_read_by_digest()
    {
        var content = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");
        var digest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        Assert.That(
            _dataPlane.PutManifest(
                _subscription,
                _resourceGroup,
                _registryName,
                "test-image",
                digest,
                content,
                "application/vnd.oci.image.manifest.v1+json"),
            Is.EqualTo(digest));

        var manifest = _dataPlane.GetManifest(
            _subscription,
            _resourceGroup,
            _registryName,
            "test-image",
            digest);

        Assert.That(manifest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(manifest!.Digest, Is.EqualTo(digest));
            Assert.That(manifest.Content, Is.EqualTo(content));
        });
    }

    public void Dispose()
    {
        var dataPath = _provider.GetServiceInstanceDataPath(
            _subscription,
            _resourceGroup,
            _registryName);
        var registryPath = Directory.GetParent(dataPath)?.FullName;
        if (registryPath is not null && Directory.Exists(registryPath))
            Directory.Delete(registryPath, recursive: true);
    }
}
