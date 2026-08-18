using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Topaz.Service.ContainerRegistry.Endpoints.Blobs;

internal static class RegistryBlobRequestBodySizeLimit
{
    internal const long MaxRequestBodySize = 1024L * 1024 * 1024;

    public static void Apply(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null)
            return;
        if (feature.IsReadOnly)
            throw new InvalidOperationException(
                "The registry blob request body limit is read-only at endpoint dispatch.");

        feature.MaxRequestBodySize = MaxRequestBodySize;
    }
}
