using Microsoft.AspNetCore.Http;
using Topaz.Service.ContainerRegistry.Endpoints.Auth;
using Topaz.Shared;

namespace Topaz.Tests.Services.ContainerRegistry;

public sealed class AcrTokenHelperTests
{
    [Test]
    public void Rejects_an_invalid_bearer_token()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer not-a-jwt";

        var objectId = AcrTokenHelper.ResolveObjectId(
            null,
            context,
            null!,
            new PrettyTopazLogger());

        Assert.That(objectId, Is.Null);
    }
}
