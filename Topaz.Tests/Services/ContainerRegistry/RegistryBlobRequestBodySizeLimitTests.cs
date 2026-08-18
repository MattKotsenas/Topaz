using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Topaz.Service.ContainerRegistry.Endpoints.Blobs;

namespace Topaz.Tests.Services.ContainerRegistry;

public sealed class RegistryBlobRequestBodySizeLimitTests
{
    [Test]
    public void Sets_the_blob_upload_request_body_limit_to_one_gibibyte()
    {
        var context = new DefaultHttpContext();
        var feature = new RequestBodySizeFeature(30_000_000, isReadOnly: false);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        RegistryBlobRequestBodySizeLimit.Apply(context);

        Assert.That(
            feature.MaxRequestBodySize,
            Is.EqualTo(1_073_741_824L));
    }

    [Test]
    public void Rejects_a_blob_upload_limit_that_is_already_read_only()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(
            new RequestBodySizeFeature(30_000_000, isReadOnly: true));

        var exception = Assert.Throws<InvalidOperationException>(
            () => RegistryBlobRequestBodySizeLimit.Apply(context));

        Assert.That(exception!.Message, Does.Contain("read-only"));
    }

    private sealed class RequestBodySizeFeature(
        long? maxRequestBodySize,
        bool isReadOnly) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; } = isReadOnly;

        public long? MaxRequestBodySize { get; set; } = maxRequestBodySize;
    }
}
