using System.IO;
using System.Linq;
using Topaz.Dns;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage.Services;
using Topaz.Shared;

namespace Topaz.Service.Storage;

/// <summary>
/// Reads a blob's content from local storage given only its public blob URI. The
/// account's subscription and resource group are recovered from the global DNS
/// registry, so the URI alone is sufficient.
/// <para>
/// This lets the resource manager resolve a deployment that references its ARM
/// template by link (<c>templateLink</c>) instead of inlining it: the link points at
/// a blob this same process serves, so it can be read directly off disk — no network,
/// no SAS round-trip.
/// </para>
/// </summary>
public static class BlobArtifactReader
{
    /// <summary>
    /// Resolves a blob URI of the form
    /// <c>https://&lt;account&gt;.blob.&lt;suffix&gt;/&lt;container&gt;/&lt;blob…&gt;</c>
    /// to the blob's text content, reconstructing the same on-disk path the blob data
    /// plane writes to. Returns <see langword="null"/> when the account is not
    /// registered or the blob file does not exist.
    /// </summary>
    public static string? TryReadBlobText(Uri blobUri, ITopazLogger logger)
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
