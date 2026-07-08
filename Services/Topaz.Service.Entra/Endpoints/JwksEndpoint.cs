using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Topaz.Identity;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.Entra.Endpoints;

/// <summary>
/// Serves the JSON Web Key Set that OIDC discovery advertises via <c>jwks_uri</c>
/// (<c>/common/discovery/v2.0/keys</c>): the public half of the Topaz signing key. With this,
/// standard clients (MSAL, the Azure SDKs, any JWKS-validating resource server) can verify the
/// RS256 signatures on Topaz-issued tokens — completing the OIDC contract Topaz already declares
/// (<c>id_token_signing_alg_values_supported: ["RS256"]</c>).
/// </summary>
internal sealed class JwksEndpoint : IEndpointDefinition
{
    public string[] Endpoints =>
    [
        "GET /common/discovery/v2.0/keys",
    ];

    public string[] Permissions => [];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol => ([GlobalSettings.DefaultResourceManagerPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        var jwk = TopazSigningKey.PublicJwk();
        var keySet = new
        {
            keys = new[]
            {
                new
                {
                    kty = jwk.Kty,
                    use = jwk.Use,
                    kid = jwk.Kid,
                    alg = jwk.Alg,
                    n = jwk.N,
                    e = jwk.E,
                },
            },
        };

        response.Content = new StringContent(JsonSerializer.Serialize(keySet, GlobalSettings.JsonOptions));
    }
}
