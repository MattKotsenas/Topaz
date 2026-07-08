using Microsoft.AspNetCore.Http;

using Topaz.EventPipeline;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.Authorization.Endpoints;

/// <summary>
/// The ARM <c>Microsoft.Authorization/permissions</c> API at resource-group scope: returns the caller's
/// effective permissions across a resource group. Previously a stub.
/// </summary>
public sealed class ResourceGroupAuthorizationEndpoint(Pipeline eventPipeline, ITopazLogger logger) : IEndpointDefinition
{
    private readonly AzureAuthorizationAdapter _adapter = new(eventPipeline, logger);

    public string? ProviderNamespace => "Microsoft.Authorization";

    public string[] Endpoints =>
    [
        "GET /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Authorization/permissions"
    ];

    public string[] Permissions => [];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        ([GlobalSettings.DefaultResourceManagerPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
        => PermissionsEndpointHelper.WritePermissions(context, response, _adapter, logger);
}