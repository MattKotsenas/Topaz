using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Topaz.ResourceManager;
using Topaz.Service.ResourceGroup;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Shared;
using Topaz.Shared.Extensions;

namespace Topaz.Service.ResourceManager;

// Generic resource passthrough: lets an ARM deployment that declares a resource type Topaz does not
// explicitly model still PERSIST that resource (keyed by its ARM id) so it is retrievable via a
// normal ARM GET, instead of being silently skipped. The store reuses the file-backed
// ResourceProviderBase; the GET is a host-level endpoint registered LAST so it only fires for
// provider/type/name paths no typed endpoint claimed.
internal sealed class GenericResourceService : IServiceDefinition
{
    public static bool IsGlobalService => false;

    // Sits one level under each resource group, parallel to the typed services' directories.
    public static string LocalDirectoryPath => Path.Combine(ResourceGroupService.LocalDirectoryPath, ".generic-resources");

    public static IReadOnlyCollection<string>? Subresources => null;

    public static string UniqueName => "genericresource";

    public string Name => "Generic Resource";

    // The generic GET is registered host-side (after the typed endpoints), not via the service list.
    public IReadOnlyCollection<IEndpointDefinition> Endpoints => [];
}

internal sealed class GenericResourceProvider(ITopazLogger logger) : ResourceProviderBase<GenericResourceService>(logger)
{
    // Persist an arbitrary deployment resource as-is, keyed by a collision-free id derived from its
    // ARM type + name (so different types/names of generic resources don't overwrite one another).
    public void Persist(GenericResource resource)
    {
        var id = BuildId(resource.Type, resource.Name);
        CreateOrUpdate(resource.GetSubscription(), resource.GetResourceGroup(), id, resource);
    }

    public GenericResource? Get(SubscriptionIdentifier subscription, ResourceGroupIdentifier resourceGroup,
        string providerNamespace, string resourceType, string resourceName)
    {
        var id = BuildId($"{providerNamespace}/{resourceType}", resourceName);
        return GetAs<GenericResource>(subscription, resourceGroup, id);
    }

    // type already contains the provider namespace + type (e.g. "Microsoft.Contoso/widgets"); combine
    // with the name and flatten the slashes so the whole thing is a single safe directory segment.
    private static string BuildId(string type, string name) => $"{type}/{name}".Replace('/', '_');
}

// First-level generic ARM resource GET. Registered host-side AFTER all typed endpoints (see Host.cs)
// so the first-match Router only selects it when no specific provider claimed the path.
public sealed class GenericResourceGetEndpoint(ITopazLogger logger) : IEndpointDefinition
{
    private readonly GenericResourceProvider _provider = new(logger);

    public string[] Endpoints =>
    [
        "GET /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/{providerNamespace}/{resourceType}/{resourceName}"
    ];

    public string[] Permissions => ["*"];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        ([GlobalSettings.DefaultResourceManagerPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        try
        {
            var path = context.Request.Path.Value!;
            var providerNamespace = path.ExtractValueFromPath(6);
            var resourceType = path.ExtractValueFromPath(7);
            var resourceName = path.ExtractValueFromPath(8);

            if (string.IsNullOrEmpty(providerNamespace) || string.IsNullOrEmpty(resourceType)
                || string.IsNullOrEmpty(resourceName))
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return;
            }

            var subscription = SubscriptionIdentifier.From(path.ExtractValueFromPath(2));
            var resourceGroup = ResourceGroupIdentifier.From(path.ExtractValueFromPath(4));

            var resource = _provider.Get(subscription, resourceGroup, providerNamespace, resourceType, resourceName);
            if (resource == null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return;
            }

            response.StatusCode = HttpStatusCode.OK;
            response.Content = new StringContent(resource.ToString());
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        catch (Exception ex)
        {
            logger.LogError(ex);
            response.StatusCode = HttpStatusCode.InternalServerError;
            response.Content = new StringContent(ex.Message);
        }
    }
}
