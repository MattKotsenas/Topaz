using Microsoft.AspNetCore.Http;

using Topaz.EventPipeline;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.Authorization.Endpoints;

/// <summary>
/// The ARM <c>Microsoft.Authorization/permissions</c> API at resource scope: returns the caller's effective
/// permissions on a specific resource (e.g. a storage account), which a data-plane enforcement point queries
/// to decide access. Previously a stub.
/// </summary>
public sealed class ResourceAuthorizationEndpoint(Pipeline eventPipeline, ITopazLogger logger) : IEndpointDefinition
{
    private readonly AzureAuthorizationAdapter _adapter = new(eventPipeline, logger);

    public string? ProviderNamespace => "Microsoft.Authorization";

    public string[] Endpoints =>
    [
        "GET /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/{providerNamespace}/{resourceType}/{resourceName}/providers/Microsoft.Authorization/permissions"
    ];

    // Any authenticated principal may read its OWN permissions, so no RBAC permission gates the endpoint; the
    // helper still requires (and identifies the caller from) a valid bearer token.
    public string[] Permissions => [];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        ([GlobalSettings.DefaultResourceManagerPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
        => PermissionsEndpointHelper.WritePermissions(context, response, _adapter, logger);
}