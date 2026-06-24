using System.Text.Json;
using Topaz.ResourceManager;
using Topaz.Service.ManagedIdentity.Models;
using Topaz.Service.ManagedIdentity.Models.Requests;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Shared;

namespace Topaz.Service.ManagedIdentity;

internal sealed class FederatedIdentityCredentialControlPlane(
    ManagedIdentityResourceProvider provider,
    ITopazLogger logger)
    : IControlPlane
{
    private const string SubresourceName = "federatedIdentityCredentials";

    private const string FicNotFoundCode = "FederatedIdentityCredentialNotFound";
    private const string FicNotFoundMessageTemplate = "Federated identity credential '{0}' could not be found";
    private const string ManagedIdentityNotFoundCode = "ManagedIdentityNotFound";
    private const string ManagedIdentityNotFoundMessageTemplate = "Managed Identity '{0}' could not be found";

    public static FederatedIdentityCredentialControlPlane New(ITopazLogger logger) =>
        new(new ManagedIdentityResourceProvider(logger), logger);

    public OperationResult Deploy(GenericResource resource)
    {
        var properties = JsonSerializer.Deserialize<CreateOrUpdateFederatedIdentityCredentialRequest.FederatedIdentityCredentialProperties>(
            JsonSerializer.Serialize(resource.Properties, GlobalSettings.JsonOptions),
            GlobalSettings.JsonOptions);
        if (properties == null)
        {
            logger.LogError($"Couldn't parse generic resource `{resource.Id}` as a federated identity credential.");
            return OperationResult.Failed;
        }

        var segments = resource.Id.Split("/", StringSplitOptions.RemoveEmptyEntries);
        var managedIdentitySegmentIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("userAssignedIdentities", StringComparison.OrdinalIgnoreCase));
        var credentialSegmentIndex = Array.FindIndex(
            segments,
            segment => segment.Equals(SubresourceName, StringComparison.OrdinalIgnoreCase));

        if (managedIdentitySegmentIndex < 0 ||
            credentialSegmentIndex < 0 ||
            managedIdentitySegmentIndex + 1 >= segments.Length ||
            credentialSegmentIndex + 1 >= segments.Length)
        {
            logger.LogError($"Couldn't parse federated identity credential path from `{resource.Id}`.");
            return OperationResult.Failed;
        }

        var result = CreateOrUpdate(
            resource.GetSubscription(),
            resource.GetResourceGroup(),
            segments[managedIdentitySegmentIndex + 1],
            segments[credentialSegmentIndex + 1],
            new CreateOrUpdateFederatedIdentityCredentialRequest
            {
                Properties = properties
            });

        return result.Result;
    }

    public ControlPlaneOperationResult<FederatedIdentityCredentialResource> CreateOrUpdate(
        SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier,
        string managedIdentityName,
        string ficName,
        CreateOrUpdateFederatedIdentityCredentialRequest request)
    {
        logger.LogDebug(nameof(FederatedIdentityCredentialControlPlane), nameof(CreateOrUpdate),
            "Executing {0}: {1} / {2}", nameof(CreateOrUpdate), managedIdentityName, ficName);
        if (request.Properties == null)
        {
            return new ControlPlaneOperationResult<FederatedIdentityCredentialResource>(
                OperationResult.Failed, null, "Request properties are required.", "InvalidRequest");
        }

        var managedIdentity = provider.GetAs<ManagedIdentityResource>(subscriptionIdentifier, resourceGroupIdentifier, managedIdentityName);
        if (managedIdentity == null)
        {
            return new ControlPlaneOperationResult<FederatedIdentityCredentialResource>(
                OperationResult.Failed, null,
                string.Format(ManagedIdentityNotFoundMessageTemplate, managedIdentityName),
                ManagedIdentityNotFoundCode);
        }

        var existing = provider.GetSubresourceAs<FederatedIdentityCredentialResource>(
            subscriptionIdentifier, resourceGroupIdentifier, ficName, managedIdentityName, SubresourceName);

        var properties = new FederatedIdentityCredentialResourceProperties
        {
            Issuer = request.Properties.Issuer,
            Subject = request.Properties.Subject,
            Audiences = request.Properties.Audiences
        };

        var resource = new FederatedIdentityCredentialResource(
            subscriptionIdentifier, resourceGroupIdentifier, managedIdentityName, ficName, properties);

        provider.CreateOrUpdateSubresource(
            subscriptionIdentifier, resourceGroupIdentifier, ficName, managedIdentityName, SubresourceName, resource);

        var result = existing == null ? OperationResult.Created : OperationResult.Updated;
        return new ControlPlaneOperationResult<FederatedIdentityCredentialResource>(result, resource, null, null);
    }

    public ControlPlaneOperationResult<FederatedIdentityCredentialResource> Get(
        SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier,
        string managedIdentityName,
        string ficName)
    {
        logger.LogDebug(nameof(FederatedIdentityCredentialControlPlane), nameof(Get),
            "Executing {0}: {1} / {2}", nameof(Get), managedIdentityName, ficName);
        var managedIdentity = provider.GetAs<ManagedIdentityResource>(subscriptionIdentifier, resourceGroupIdentifier, managedIdentityName);
        if (managedIdentity == null)
        {
            return new ControlPlaneOperationResult<FederatedIdentityCredentialResource>(
                OperationResult.NotFound, null,
                string.Format(ManagedIdentityNotFoundMessageTemplate, managedIdentityName),
                ManagedIdentityNotFoundCode);
        }

        var resource = provider.GetSubresourceAs<FederatedIdentityCredentialResource>(
            subscriptionIdentifier, resourceGroupIdentifier, ficName, managedIdentityName, SubresourceName);

        return resource == null
            ? new ControlPlaneOperationResult<FederatedIdentityCredentialResource>(
                OperationResult.NotFound, null,
                string.Format(FicNotFoundMessageTemplate, ficName),
                FicNotFoundCode)
            : new ControlPlaneOperationResult<FederatedIdentityCredentialResource>(
                OperationResult.Success, resource, null, null);
    }

    public ControlPlaneOperationResult Delete(
        SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier,
        string managedIdentityName,
        string ficName)
    {
        logger.LogDebug(nameof(FederatedIdentityCredentialControlPlane), nameof(Delete),
            "Executing {0}: {1} / {2}", nameof(Delete), managedIdentityName, ficName);
        var managedIdentity = provider.GetAs<ManagedIdentityResource>(subscriptionIdentifier, resourceGroupIdentifier, managedIdentityName);
        if (managedIdentity == null)
        {
            return new ControlPlaneOperationResult(
                OperationResult.NotFound,
                string.Format(ManagedIdentityNotFoundMessageTemplate, managedIdentityName),
                ManagedIdentityNotFoundCode);
        }

        var existing = provider.GetSubresourceAs<FederatedIdentityCredentialResource>(
            subscriptionIdentifier, resourceGroupIdentifier, ficName, managedIdentityName, SubresourceName);

        if (existing == null)
        {
            return new ControlPlaneOperationResult(
                OperationResult.NotFound,
                string.Format(FicNotFoundMessageTemplate, ficName),
                FicNotFoundCode);
        }

        provider.DeleteSubresource(subscriptionIdentifier, resourceGroupIdentifier, ficName, managedIdentityName, SubresourceName);
        return new ControlPlaneOperationResult(OperationResult.Deleted, null, null);
    }

    public ControlPlaneOperationResult<FederatedIdentityCredentialResource[]> List(
        SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier,
        string managedIdentityName)
    {
        logger.LogDebug(nameof(FederatedIdentityCredentialControlPlane), nameof(List),
            "Executing {0}: {1}", nameof(List), managedIdentityName);
        var managedIdentity = provider.GetAs<ManagedIdentityResource>(subscriptionIdentifier, resourceGroupIdentifier, managedIdentityName);
        if (managedIdentity == null)
        {
            return new ControlPlaneOperationResult<FederatedIdentityCredentialResource[]>(
                OperationResult.NotFound, null,
                string.Format(ManagedIdentityNotFoundMessageTemplate, managedIdentityName),
                ManagedIdentityNotFoundCode);
        }

        var resources = provider.ListSubresourcesAs<FederatedIdentityCredentialResource>(
            subscriptionIdentifier, resourceGroupIdentifier, managedIdentityName, SubresourceName);

        return new ControlPlaneOperationResult<FederatedIdentityCredentialResource[]>(
            OperationResult.Success, resources, null, null);
    }
}
