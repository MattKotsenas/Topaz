using Microsoft.AspNetCore.Http;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.ContainerRegistry.Endpoints.Auth;

internal sealed class AuthenticatedAcrDataPlaneEndpoint(
    IEndpointDefinition inner,
    AcrDataPlaneAuthenticator authenticator) : IEndpointDefinition
{
    public string? ProviderNamespace => inner.ProviderNamespace;

    public string[] Endpoints => inner.Endpoints;

    public string[] Permissions => inner.Permissions;

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        inner.PortsAndProtocol;

    public void GetResponse(
        HttpContext context,
        HttpResponseMessage response,
        GlobalOptions options)
    {
        if (authenticator.AuthenticateOrChallenge(context, response))
            inner.GetResponse(context, response, options);
    }
}
