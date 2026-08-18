using Microsoft.AspNetCore.Http;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.ContainerRegistry.Endpoints.Auth;

/// <summary>
/// Implements the Docker Registry V2 ping endpoint through the shared data-plane authenticator.
/// </summary>
internal sealed class AcrV2ChallengeEndpoint(AcrDataPlaneAuthenticator authenticator) : IEndpointDefinition
{
    public string? ProviderNamespace => "Microsoft.ContainerRegistry";

    public string[] Endpoints => ["GET /v2/"];

    public string[] Permissions => [];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        ([GlobalSettings.ContainerRegistryPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        if (authenticator.AuthenticateOrChallenge(context, response))
            response.CreateJsonContentResponse("{}");
    }
}
