using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Topaz.EventPipeline;
using Topaz.Identity;
using Topaz.Service.ManagedIdentity.Models;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Subscription;
using Topaz.Shared;

namespace Topaz.Service.ManagedIdentity.Endpoints;

/// <summary>
/// Issues a managed-identity access token over the IMDS / App Service MSI contract
/// (<c>GET /metadata/identity/oauth2/token?resource=&amp;client_id=</c>), so Topaz is the single issuer of MI
/// tokens rather than a local component self-minting them. Resolves the user-assigned identity by
/// <c>client_id</c> from Topaz's own store, then signs an RS256 token (JWKS-verifiable) carrying the MI claims
/// (sub/oid = principal id, appid/azp = client id, aud = resource, xms_mirid = the identity's ARM id). A local
/// IMDS proxy forwards its workload's request here. Unauthenticated by contract (like real IMDS - trust is the
/// local network boundary).
/// </summary>
internal sealed class ManagedIdentityTokenEndpoint(Pipeline eventPipeline, ITopazLogger logger) : IEndpointDefinition
{
    private readonly SubscriptionControlPlane _subscriptions = SubscriptionControlPlane.New(eventPipeline, logger);
    private readonly ManagedIdentityControlPlane _identities = ManagedIdentityControlPlane.New(eventPipeline, logger);

    public string[] Endpoints => ["GET /metadata/identity/oauth2/token"];

    public string[] Permissions => [];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        ([GlobalSettings.DefaultResourceManagerPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        var resource = context.Request.Query["resource"].ToString();
        var clientId = context.Request.Query["client_id"].ToString();

        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(clientId))
        {
            WriteError(response, "invalid_request",
                "Both 'resource' and 'client_id' query parameters are required.");
            return;
        }

        var identity = ResolveByClientId(clientId);
        if (identity?.Properties.PrincipalId is not { Length: > 0 } principalId)
        {
            logger.LogDebug(nameof(ManagedIdentityTokenEndpoint), nameof(GetResponse),
                "No user-assigned managed identity with client_id `{0}` was found.", clientId);
            WriteError(response, "invalid_request",
                $"No user-assigned managed identity with client_id '{clientId}' was found.");
            return;
        }

        var token = JwtHelper.CreateManagedIdentityToken(principalId, clientId, resource, identity.Id);
        var expiresOn = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var body = JsonSerializer.Serialize(new
        {
            access_token = token,
            client_id = clientId,
            expires_in = "3599",
            expires_on = expiresOn.ToString(),
            resource,
            token_type = "Bearer"
        }, GlobalSettings.JsonOptions);

        response.StatusCode = HttpStatusCode.OK;
        response.Content = new StringContent(body);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    // Storage-account-style global lookup: managed-identity client ids are unique, so scan each subscription's
    // user-assigned identities for the matching client id. Emulator scale is small and MI tokens cache for an
    // hour, so the enumeration cost is negligible.
    private ManagedIdentityResource? ResolveByClientId(string clientId)
    {
        var subscriptions = _subscriptions.List().Resource ?? [];
        foreach (var subscription in subscriptions)
        {
            var identities = _identities.ListBySubscription(SubscriptionIdentifier.From(subscription.SubscriptionId));
            var match = (identities.Resource ?? [])
                .FirstOrDefault(i => string.Equals(i.Properties.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void WriteError(HttpResponseMessage response, string error, string description)
    {
        response.StatusCode = HttpStatusCode.BadRequest;
        response.Content = new StringContent(
            JsonSerializer.Serialize(new { error, error_description = description }, GlobalSettings.JsonOptions));
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }
}
