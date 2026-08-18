using System.Net;
using Microsoft.AspNetCore.Http;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.ContainerRegistry.Endpoints.Auth;

internal sealed class AcrDataPlaneAuthenticator(
    ContainerRegistryControlPlane controlPlane,
    ITopazLogger logger)
{
    public bool AuthenticateOrChallenge(
        HttpContext context,
        HttpResponseMessage response)
    {
        if (AcrTokenHelper.ResolveObjectId(
                null,
                context,
                controlPlane,
                logger) is not null)
        {
            return true;
        }

        var host = context.Request.Host.Value;
        response.Headers.Add(
            "Www-Authenticate",
            $"Bearer realm=\"https://{host}/oauth2/token\",service=\"{host}\"");
        response.CreateJsonContentResponse(
            "{\"errors\":[{\"code\":\"UNAUTHORIZED\",\"message\":\"authentication required\"}]}",
            HttpStatusCode.Unauthorized);
        return false;
    }
}
