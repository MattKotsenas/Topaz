using System.Security.Cryptography;
using Topaz.Service.ContainerRegistry;
using Topaz.Service.Shared.Domain;
using Topaz.Shared;

namespace Topaz.Tests.Services.ContainerRegistry;

public sealed class AcrDataPlaneBlobConcurrencyTests : IDisposable
{
    private readonly SubscriptionIdentifier _subscription =
        SubscriptionIdentifier.From(Guid.NewGuid());
    private readonly ResourceGroupIdentifier _resourceGroup =
        ResourceGroupIdentifier.From("registry-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _registryName =
        "registry" + Guid.NewGuid().ToString("N")[..12];
    private readonly ContainerRegistryResourceProvider _provider;
    private readonly AcrDataPlane _dataPlane;

    public AcrDataPlaneBlobConcurrencyTests()
    {
        var logger = new PrettyTopazLogger();
        _provider = new ContainerRegistryResourceProvider(logger);
        _dataPlane = new AcrDataPlane(_provider, logger);
    }

    [Test]
    public void Delete_succeeds_while_a_blob_download_is_open()
    {
        var payload = RandomNumberGenerator.GetBytes(1024);
        var digest = Upload(payload);
        using var download = _dataPlane.OpenBlob(
            _subscription,
            _resourceGroup,
            _registryName,
            digest);
        Assert.That(download, Is.Not.Null);

        Assert.That(
            _dataPlane.DeleteBlob(
                _subscription,
                _resourceGroup,
                _registryName,
                digest),
            Is.True);
        Assert.That(
            _dataPlane.OpenBlob(
                _subscription,
                _resourceGroup,
                _registryName,
                digest),
            Is.Null);
        Assert.That(
            _dataPlane.GetBlobLength(
                _subscription,
                _resourceGroup,
                _registryName,
                digest),
            Is.Null);
        Assert.That(ReadAll(download!), Is.EqualTo(payload));
    }

    [Test]
    public void Completing_duplicate_content_succeeds_while_a_blob_download_is_open()
    {
        var payload = RandomNumberGenerator.GetBytes(1024);
        var digest = Upload(payload);
        using var download = _dataPlane.OpenBlob(
            _subscription,
            _resourceGroup,
            _registryName,
            digest);
        Assert.That(download, Is.Not.Null);

        Assert.That(Upload(payload), Is.EqualTo(digest));
        Assert.That(ReadAll(download!), Is.EqualTo(payload));
        Assert.That(
            _dataPlane.BlobExists(
                _subscription,
                _resourceGroup,
                _registryName,
                digest),
            Is.True);
    }

    [Test]
    public async Task Concurrent_duplicate_completions_both_succeed()
    {
        var payload = RandomNumberGenerator.GetBytes(1024);
        var digest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var firstUpload = PrepareUpload();
        var secondUpload = PrepareUpload();
        using var barrier = new Barrier(2);

        var completions = await Task.WhenAll(
            Task.Run(() => Complete(firstUpload, payload, digest, barrier)),
            Task.Run(() => Complete(secondUpload, payload, digest, barrier)));

        Assert.That(completions, Is.All.EqualTo(digest));
        Assert.That(
            _dataPlane.BlobExists(
                _subscription,
                _resourceGroup,
                _registryName,
                digest),
            Is.True);
    }

    [Test]
    public async Task Concurrent_delete_and_duplicate_completion_preserve_a_valid_state()
    {
        var payload = RandomNumberGenerator.GetBytes(1024);
        var digest = Upload(payload);
        var upload = PrepareUpload();
        using var barrier = new Barrier(2);

        await Task.WhenAll(
            Task.Run(() =>
            {
                barrier.SignalAndWait();
                _dataPlane.DeleteBlob(
                    _subscription,
                    _resourceGroup,
                    _registryName,
                    digest);
            }),
            Task.Run(() => Complete(upload, payload, digest, barrier)));

        using var blob = _dataPlane.OpenBlob(
            _subscription,
            _resourceGroup,
            _registryName,
            digest);
        if (blob is not null)
            Assert.That(ReadAll(blob), Is.EqualTo(payload));
    }

    [Test]
    public async Task Concurrent_blob_lookups_and_delete_return_valid_results()
    {
        var payload = RandomNumberGenerator.GetBytes(1024);
        var digest = Upload(payload);
        using var barrier = new Barrier(3);

        var openTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return _dataPlane.OpenBlob(
                _subscription,
                _resourceGroup,
                _registryName,
                digest);
        });
        var lengthTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return _dataPlane.GetBlobLength(
                _subscription,
                _resourceGroup,
                _registryName,
                digest);
        });
        var deleteTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return _dataPlane.DeleteBlob(
                _subscription,
                _resourceGroup,
                _registryName,
                digest);
        });

        await Task.WhenAll(openTask, lengthTask, deleteTask);

        Assert.That(await deleteTask, Is.True);
        using var blob = await openTask;
        if (blob is not null)
            Assert.That(ReadAll(blob), Is.EqualTo(payload));
        if (await lengthTask is { } length)
            Assert.That(length, Is.EqualTo(payload.Length));
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

    private string Upload(byte[] payload)
    {
        var upload = PrepareUpload();
        var digest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var content = new MemoryStream(payload);
        return _dataPlane.CompleteUpload(
                   _subscription,
                   _resourceGroup,
                   _registryName,
                   upload,
                   digest,
                   content)
               ?? throw new InvalidOperationException("Blob upload digest did not match.");
    }

    private string PrepareUpload() =>
        _dataPlane.InitiateUpload(
            _subscription,
            _resourceGroup,
            _registryName);

    private string Complete(
        string upload,
        byte[] payload,
        string digest,
        Barrier barrier)
    {
        barrier.SignalAndWait();
        using var content = new MemoryStream(payload);
        return _dataPlane.CompleteUpload(
                   _subscription,
                   _resourceGroup,
                   _registryName,
                   upload,
                   digest,
                   content)
               ?? throw new InvalidOperationException("Blob upload digest did not match.");
    }

    private static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        using var content = new MemoryStream();
        stream.CopyTo(content);
        return content.ToArray();
    }
}
