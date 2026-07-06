using System.IO;
using System.Linq;
using System.Net.Http;
using Topaz.Dns;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage.Services;
using Topaz.Shared;

namespace Topaz.Service.Storage;

/// <summary>
/// Reads a blob's text content given only its public blob URI, so the resource manager can resolve a
/// deployment that references its ARM template by link (<c>templateLink</c>) instead of inlining it.
/// <para>
/// Two modes, selected by <see cref="Topaz.Shared.GlobalSettings.ArtifactFetchEndpoint"/>:
/// </para>
/// <list type="bullet">
/// <item>Local disk (default): the linked blob is served by this same process, so it is read straight
/// off the storage data path - the account's subscription and resource group are recovered from the
/// global DNS registry, so the URI alone suffices - with no network and no SAS round-trip.</item>
/// <item>Network (when a front-door endpoint is configured): the blob is served by an external storage
/// backend, so it is fetched over HTTP through that front door, carrying the link's original account
/// host so the front door routes it to the right account and the link's SAS authorizes the read.</item>
/// </list>
/// </summary>
public static class BlobArtifactReader
{
    /// <summary>
    /// Resolves a blob URI of the form
    /// <c>https://&lt;account&gt;.blob.&lt;suffix&gt;/&lt;container&gt;/&lt;blob…&gt;</c>
    /// to the blob's text content, either off local disk or over the network (see the type summary).
    /// Returns <see langword="null"/> when the blob cannot be resolved.
    /// </summary>
    public static string? TryReadBlobText(Uri blobUri, ITopazLogger logger)
    {
        // When a front-door endpoint is configured the linked blob is served by an external storage
        // backend, so fetch it over the network; otherwise it is on this process's own storage disk.
        return GlobalSettings.ArtifactFetchEndpoint is { } frontDoor
            ? TryFetchBlobText(blobUri, frontDoor, logger)
            : TryReadBlobTextFromDisk(blobUri, logger);
    }

    // The front door is the emulator's own storage entry point (loopback / host-local); accept its
    // self-signed cert. The templateLink's SAS still authorizes the read.
    private static readonly HttpClient FetchClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Builds the request that fetches <paramref name="templateLink"/> through <paramref name="frontDoor"/>:
    /// the front door's scheme/host/port with the link's path + query (its SAS) preserved verbatim, and the
    /// link's own host returned as the Host header so the front door routes to the correct account. Pure; no
    /// I/O. Returns <see langword="false"/> for a non-absolute link or front door, or a link with no host.
    /// </summary>
    public static bool TryBuildFetchRequest(Uri templateLink, Uri frontDoor, out Uri fetchUri, out string hostHeader)
    {
        fetchUri = null!;
        hostHeader = string.Empty;

        if (templateLink is not { IsAbsoluteUri: true } || frontDoor is not { IsAbsoluteUri: true }
            || string.IsNullOrEmpty(templateLink.Host))
        {
            return false;
        }

        var candidate = frontDoor.GetLeftPart(UriPartial.Authority) + templateLink.PathAndQuery;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out fetchUri!))
        {
            return false;
        }

        hostHeader = templateLink.Host;
        return true;
    }

    private static string? TryFetchBlobText(Uri templateLink, Uri frontDoor, ITopazLogger logger)
    {
        if (!TryBuildFetchRequest(templateLink, frontDoor, out var fetchUri, out var hostHeader))
        {
            logger.LogDebug(nameof(BlobArtifactReader), nameof(TryFetchBlobText),
                "Cannot build a fetch request for {0} via {1}.", templateLink, frontDoor);
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fetchUri);
            request.Headers.Host = hostHeader;
            using var response = FetchClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(nameof(BlobArtifactReader), nameof(TryFetchBlobText),
                    "Fetch of {0} via {1} returned {2}.", templateLink, frontDoor, (int)response.StatusCode);
                return null;
            }

            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogDebug(nameof(BlobArtifactReader), nameof(TryFetchBlobText),
                "Fetch of {0} via {1} failed: {2}", templateLink, frontDoor, ex.Message);
            return null;
        }
    }

    private static string? TryReadBlobTextFromDisk(Uri blobUri, ITopazLogger logger)
    {
        // "<account>.blob.<suffix>" — the account is the first DNS label.
        var accountName = blobUri.Host.Split('.')[0];
        if (string.IsNullOrEmpty(accountName))
        {
            return null;
        }

        var entry = GlobalDnsEntries.GetEntry(AzureStorageService.UniqueName, accountName);
        if (entry == null || entry.Value.resourceGroup == null)
        {
            logger.LogDebug(nameof(BlobArtifactReader), nameof(TryReadBlobText),
                "Storage account '{0}' is not registered; cannot resolve {1}.", accountName, blobUri);
            return null;
        }

        // First path segment is the container; the rest is the blob's sub-path.
        var segments = blobUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        var containerName = segments[0];
        var provider = new BlobResourceProvider(logger);
        var containerDataPath = provider.GetContainerDataPath(
            SubscriptionIdentifier.From(entry.Value.subscription),
            ResourceGroupIdentifier.From(entry.Value.resourceGroup),
            accountName,
            containerName);

        // Mirror the blob data plane's layout: <containerDataPath>/<sanitized blob segments>.
        var blobSegments = segments.Skip(1).Select(PathGuard.SanitizeName);
        var blobFile = Path.Combine(new[] { containerDataPath }.Concat(blobSegments).ToArray());

        if (!File.Exists(blobFile))
        {
            logger.LogDebug(nameof(BlobArtifactReader), nameof(TryReadBlobText),
                "Blob file '{0}' not found for {1}.", blobFile, blobUri);
            return null;
        }

        return File.ReadAllText(blobFile);
    }
}
