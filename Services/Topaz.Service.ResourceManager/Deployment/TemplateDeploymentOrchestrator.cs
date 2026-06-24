using System.Text.Json;
using Azure.Core;
using Azure.Deployments.Core.Definitions.Schema;
using Azure.Deployments.Core.Entities;
using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Extensions;
using Newtonsoft.Json.Linq;
using Topaz.EventPipeline;
using Topaz.ResourceManager;
using Topaz.Service.Authorization;
using Topaz.Service.AppService;
using Topaz.Service.ContainerRegistry;
using Topaz.Service.EventHub;
using Topaz.Service.KeyVault;
using Topaz.Service.ManagedIdentity;
using Topaz.Service.ResourceManager.Models;
using Topaz.Service.ResourceGroup;
using Topaz.Service.ResourceGroup.Models;
using Topaz.Service.ServiceBus;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Service.Sql;
using Topaz.Service.CosmosDb;
using Topaz.Service.Disk;
using Topaz.Service.LoadBalancer;
using Topaz.Service.VirtualMachine;
using Topaz.Service.VirtualNetwork;
using Topaz.Shared;
using DeploymentResource = Topaz.Service.ResourceManager.Models.DeploymentResource;

namespace Topaz.Service.ResourceManager.Deployment;

public sealed class TemplateDeploymentOrchestrator(
    Pipeline eventPipeline,
    ResourceManagerResourceProvider rgProvider,
    SubscriptionDeploymentResourceProvider subProvider,
    TenantDeploymentResourceProvider tenantProvider,
    ManagementGroupDeploymentResourceProvider mgProvider,
    ITopazLogger logger)
{
    private static readonly List<TemplateDeployment> DeploymentQueue = [];
    private static readonly Lock QueueLock = new();
    private static string? _currentDeploymentId;
    private static CancellationTokenSource? _currentCts;
    private static Thread? OrchestratorThread { get; set; }

    private readonly ArmTemplateEngineFacade _armTemplateEngineFacade = new();

    public void EnqueueTemplateDeployment(
        SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier,
        Template template,
        DeploymentResource deploymentResource,
        InsensitiveDictionary<JToken> metadataInsensitive)
    {
        _armTemplateEngineFacade.ProcessTemplate(subscriptionIdentifier, resourceGroupIdentifier, template,
            metadataInsensitive, deploymentResource.Properties.Parameters);

        foreach (var resource in template.Resources)
        {
            resource.Id = new TemplateGenericProperty<string>
            {
                Value = BuildResourceGroupScopeId(
                    subscriptionIdentifier.Value.ToString(),
                    resourceGroupIdentifier.Value,
                    resource.Type.Value,
                    resource.Name.Value)
            };
        }

        var job = new TemplateDeployment(
            deploymentResource.Id, deploymentResource.Name, template,
            complete: deploymentResource.CompleteDeployment,
            cancel: deploymentResource.CancelDeployment,
            fail: deploymentResource.FailDeployment,
            persist: () => rgProvider.CreateOrUpdate(subscriptionIdentifier, resourceGroupIdentifier,
                deploymentResource.Name, deploymentResource),
            setOutputs: outputs => deploymentResource.Properties.Outputs = outputs,
            metadata: metadataInsensitive,
            parameters: deploymentResource.Properties.Parameters,
            setError: error => deploymentResource.Properties.Error = error);

        lock (QueueLock) { DeploymentQueue.Add(job); }
    }

    public void EnqueueSubscriptionDeployment(
        SubscriptionIdentifier subscriptionIdentifier,
        Template template,
        SubscriptionDeploymentResource deploymentResource,
        InsensitiveDictionary<JToken> metadataInsensitive)
    {
        _armTemplateEngineFacade.ProcessTemplateAtSubscriptionScope(subscriptionIdentifier, template,
            metadataInsensitive, deploymentResource.Properties.Parameters);

        foreach (var resource in template.Resources)
        {
            // Resource groups use /subscriptions/{sub}/resourceGroups/{name} path format
            if (resource.Type.Value == "Microsoft.Resources/resourceGroups")
            {
                resource.Id = new TemplateGenericProperty<string>
                {
                    Value = $"/subscriptions/{subscriptionIdentifier}/resourceGroups/{resource.Name}"
                };
            }
            else
            {
                resource.Id = new TemplateGenericProperty<string>
                {
                    Value = BuildSubscriptionScopeId(subscriptionIdentifier.Value.ToString(), resource.Type.Value, resource.Name.Value)
                };
            }
        }

        var job = new TemplateDeployment(
            deploymentResource.Id, deploymentResource.Name, template,
            complete: deploymentResource.CompleteDeployment,
            cancel: deploymentResource.CancelDeployment,
            fail: deploymentResource.FailDeployment,
            persist: () => subProvider.CreateOrUpdate(subscriptionIdentifier, null,
                deploymentResource.Name, deploymentResource),
            setOutputs: outputs => deploymentResource.Properties.Outputs = outputs,
            metadata: metadataInsensitive,
            parameters: deploymentResource.Properties.Parameters,
            setError: error => deploymentResource.Properties.Error = error);

        lock (QueueLock) { DeploymentQueue.Add(job); }
    }

    public void EnqueueTenantDeployment(
        Template template,
        TenantDeploymentResource deploymentResource,
        InsensitiveDictionary<JToken> metadataInsensitive)
    {
        _armTemplateEngineFacade.ProcessTemplateAtTenantScope(template,
            metadataInsensitive, deploymentResource.Properties.Parameters);

        foreach (var resource in template.Resources)
        {
            resource.Id = new TemplateGenericProperty<string>
            {
                Value = $"/providers/{resource.Type.Value}/{resource.Name.Value}"
            };
        }

        var job = new TemplateDeployment(
            deploymentResource.Id, deploymentResource.Name, template,
            complete: deploymentResource.CompleteDeployment,
            cancel: deploymentResource.CancelDeployment,
            fail: deploymentResource.FailDeployment,
            persist: () => tenantProvider.CreateOrUpdateDeployment(deploymentResource.Name, deploymentResource),
            setOutputs: outputs => deploymentResource.Properties.Outputs = outputs,
            metadata: metadataInsensitive,
            parameters: deploymentResource.Properties.Parameters,
            setError: error => deploymentResource.Properties.Error = error);

        lock (QueueLock) { DeploymentQueue.Add(job); }
    }

    public void EnqueueManagementGroupDeployment(
        string groupId,
        Template template,
        ManagementGroupDeploymentResource deploymentResource,
        InsensitiveDictionary<JToken> metadataInsensitive)
    {
        _armTemplateEngineFacade.ProcessTemplateAtManagementGroupScope(template,
            metadataInsensitive, deploymentResource.Properties.Parameters);

        foreach (var resource in template.Resources)
        {
            resource.Id = new TemplateGenericProperty<string>
            {
                Value = $"/providers/{resource.Type.Value}/{resource.Name.Value}"
            };
        }

        var job = new TemplateDeployment(
            deploymentResource.Id, deploymentResource.Name, template,
            complete: deploymentResource.CompleteDeployment,
            cancel: deploymentResource.CancelDeployment,
            fail: deploymentResource.FailDeployment,
            persist: () => mgProvider.CreateOrUpdateDeployment(groupId, deploymentResource.Name, deploymentResource),
            setOutputs: outputs => deploymentResource.Properties.Outputs = outputs,
            metadata: metadataInsensitive,
            parameters: deploymentResource.Properties.Parameters,
            setError: error => deploymentResource.Properties.Error = error);

        lock (QueueLock) { DeploymentQueue.Add(job); }
    }


    public OperationResult CancelDeployment(string deploymentId)
    {
        TemplateDeployment? toCancel;
        lock (QueueLock)
        {
            if (_currentDeploymentId == deploymentId)
            {
                // Signal the running deployment's CancellationToken; RouteDeployment will
                // detect it after the current resource completes and transition to Canceled.
                _currentCts?.Cancel();
                return OperationResult.Success;
            }

            toCancel = DeploymentQueue.FirstOrDefault(d => d.Id == deploymentId);
            if (toCancel == null)
                return OperationResult.Conflict;

            DeploymentQueue.RemoveAll(d => d.Id == deploymentId);
        }

        toCancel.Cancel();
        toCancel.Persist();
        return OperationResult.Success;
    }

    public void Start(CancellationToken stoppingToken = default)
    {
        if (OrchestratorThread != null)
            throw new InvalidOperationException("Orchestrator thread already running");

        OrchestratorThread = new Thread(() =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TemplateDeployment? deployment = null;
                lock (QueueLock)
                {
                    if (DeploymentQueue.Count > 0)
                    {
                        deployment = DeploymentQueue[0];
                        DeploymentQueue.RemoveAt(0);
                        _currentDeploymentId = deployment.Id;
                    }
                }

                if (deployment == null)
                {
                    logger.LogDebug(nameof(TemplateDeploymentOrchestrator), nameof(Start),"No deployments in the queue, will attempt to check again in 10 seconds...");
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    continue;
                }

                logger.LogDebug(nameof(TemplateDeploymentOrchestrator), nameof(Start),
                    "Fetched deployment: {0}", deployment.Id);

                var cts = new CancellationTokenSource();
                deployment.SetCancellationTokenSource(cts);
                lock (QueueLock) { _currentCts = cts; }

                try
                {
                    RouteDeployment(deployment);
                }
                catch (Exception ex)
                {
                    logger.LogError(nameof(TemplateDeploymentOrchestrator), nameof(Start),
                        "Unhandled exception on deployment background thread for '{0}': {1}", deployment.Id, ex.Message);
                    deployment.SetError(new DeploymentErrorInfo { Code = "DeploymentFailed", Message = ex.Message });
                    deployment.Fail();
                    deployment.Persist();
                }
                finally
                {
                    lock (QueueLock)
                    {
                        _currentDeploymentId = null;
                        _currentCts = null;
                    }
                    cts.Dispose();
                }
            }
        });

        OrchestratorThread.Start();
    }

    private void RouteDeployment(TemplateDeployment templateDeployment)
    {
        logger.LogDebug(nameof(TemplateDeploymentOrchestrator), nameof(RouteDeployment),
            "Routing deployment resources of {0} to appropriate control planes.", templateDeployment.Id);

        templateDeployment.Start();
        logger.LogInformation($"Deployment of {templateDeployment.Id} started.");

        var hasProvisioningFailed = false;
        // Process resource groups first to ensure they exist before dependent resources are deployed
        var orderedResources = templateDeployment.Template.Resources
            .OrderByDescending(r => (r.Type?.Value ?? string.Empty).Equals("Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        foreach (var resource in orderedResources)
        {
            IControlPlane? controlPlane = null;
            var evaluatedResourceJson = GetEvaluatedResourceJson(templateDeployment, resource);
            var genericResource =
                JsonSerializer.Deserialize<GenericResource>(evaluatedResourceJson, GlobalSettings.JsonOptions)!;

            // resource.Type.Value may be null after subscription-scope template processing for
            // certain resource types (e.g. Microsoft.Resources/deployments); fall back to the
            // type string preserved in the deserialized GenericResource.
            var resourceType = resource.Type?.Value ?? genericResource.Type ?? string.Empty;
            var resourceName = resource.Name?.Value ?? genericResource.Name ?? string.Empty;
            genericResource = NormalizeGenericResource(genericResource, resource, templateDeployment, resourceType, resourceName);

            switch (resourceType)
            {
                case "Microsoft.ContainerRegistry/registries":
                    controlPlane = ContainerRegistryControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.KeyVault/vaults":
                    controlPlane = KeyVaultControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Network/virtualNetworks":
                    controlPlane = VirtualNetworkControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Network/networkSecurityGroups":
                    controlPlane = NetworkSecurityGroupControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Network/networkInterfaces":
                    controlPlane = NetworkInterfaceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Network/publicIPAddresses":
                    controlPlane = PublicIpAddressControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Network/loadBalancers":
                    controlPlane = LoadBalancerControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Compute/virtualMachines":
                    controlPlane = VirtualMachineServiceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Compute/disks":
                    controlPlane = DiskServiceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.ManagedIdentity/userAssignedIdentities":
                    controlPlane = ManagedIdentityControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials":
                    controlPlane = FederatedIdentityCredentialControlPlane.New(logger);
                    break;
                case "Microsoft.Authorization/roleAssignments":
                    controlPlane = AuthorizationControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.EventHub/namespaces":
                    controlPlane = EventHubServiceControlPlane.New(logger);
                    break;
                case "Microsoft.ServiceBus/namespaces":
                    controlPlane = ServiceBusServiceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Storage/storageAccounts":
                    controlPlane = AzureStorageControlPlane.New(logger);
                    break;
                case "Microsoft.Web/serverfarms":
                    controlPlane = AppServicePlanControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Web/sites":
                    controlPlane = AppServiceSiteControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Sql/servers":
                    controlPlane = SqlServiceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Sql/servers/databases":
                    controlPlane = SqlServiceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.DocumentDB/databaseAccounts":
                    controlPlane = CosmosDbServiceControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Resources/resourceGroups":
                    controlPlane = ResourceGroupControlPlane.New(eventPipeline, logger);
                    break;
                case "Microsoft.Resources/deployments":
                    HandleNestedDeployment(genericResource, templateDeployment, evaluatedResourceJson, ref hasProvisioningFailed);
                    break;
                default:
                    logger.LogInformation(
                        $"Resource type {resourceType} is not explicitly modeled; persisting it via the generic " +
                        "resource passthrough so it remains retrievable.");
                    new GenericResourceProvider(logger).Persist(genericResource);
                    break;
            }

            var result = controlPlane?.Deploy(genericResource);
            logger.LogInformation($"Deployment of {genericResource.Id} completed with status {result}.");

            if (result == OperationResult.Failed)
                hasProvisioningFailed = true;

            if (templateDeployment.CancellationToken.IsCancellationRequested)
            {
                templateDeployment.Cancel();
                templateDeployment.Persist();
                logger.LogInformation($"Deployment {templateDeployment.Id} was cancelled mid-flight after provisioning {genericResource.Id}.");
                return;
            }
        }

        // Evaluate and set template outputs on the deployment
        if (templateDeployment.Template.Outputs != null)
        {
            // Parse subscription ID and resource group name from the deployment ID so the
            // expression evaluation context matches the scope used during template processing.
            var idParts = templateDeployment.Id.TrimStart('/').Split('/');
            var subscriptionId = idParts.Length > 1 && idParts[0] == "subscriptions" ? idParts[1] : string.Empty;
            var resourceGroupName = idParts.Length > 3 && idParts[2] == "resourceGroups" ? idParts[3] : string.Empty;

            var outputsJObject = _armTemplateEngineFacade.EvaluateOutputs(subscriptionId, resourceGroupName, templateDeployment.Template, logger);
            var outputsJson = outputsJObject.ToString(Newtonsoft.Json.Formatting.None);
            var outputs = JsonDocument.Parse(outputsJson).RootElement.Clone();
            templateDeployment.SetOutputs(outputs);
        }

        if (!hasProvisioningFailed)
            templateDeployment.Complete();
        else
            templateDeployment.Fail();

        templateDeployment.Persist();
        logger.LogInformation($"Deployment {templateDeployment.Id} completed.");
    }

    private GenericResource NormalizeGenericResource(
        GenericResource genericResource,
        TemplateResource templateResource,
        TemplateDeployment templateDeployment,
        string resourceType,
        string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(resourceName))
            return genericResource;

        var (subscriptionId, resourceGroupName) = GetDeploymentScope(templateDeployment.Id);
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return genericResource;

        var id = resourceType.Equals("Microsoft.Authorization/roleAssignments", StringComparison.OrdinalIgnoreCase)
            ? BuildRoleAssignmentId(subscriptionId, resourceGroupName, templateResource, resourceName)
            : resourceGroupName == null
                ? BuildSubscriptionScopeId(subscriptionId, resourceType, resourceName)
                : BuildResourceGroupScopeId(subscriptionId, resourceGroupName, resourceType, resourceName);

        return new GenericResource
        {
            Id = id,
            Name = resourceName,
            Type = resourceType,
            Location = genericResource.Location,
            Tags = genericResource.Tags,
            Sku = genericResource.Sku,
            Kind = genericResource.Kind,
            Properties = genericResource.Properties,
            Identity = genericResource.Identity
        };
    }

    private string GetEvaluatedResourceJson(TemplateDeployment templateDeployment, TemplateResource resource)
    {
        var (subscriptionId, resourceGroupName) = GetDeploymentScope(templateDeployment.Id);
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return resource.ToJson();

        var resourceObject = _armTemplateEngineFacade.EvaluateResource(
            subscriptionId,
            resourceGroupName ?? string.Empty,
            templateDeployment.Template,
            resource,
            logger);

        return resourceObject.ToString(Newtonsoft.Json.Formatting.None);
    }

    private string BuildRoleAssignmentId(
        string subscriptionId,
        string? resourceGroupName,
        TemplateResource templateResource,
        string resourceName)
    {
        var scope = BuildRoleAssignmentScope(subscriptionId, resourceGroupName, templateResource.Scope?.Value);
        var roleAssignmentName = resourceName.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        return $"{scope}/providers/Microsoft.Authorization/roleAssignments/{roleAssignmentName}";
    }

    private string BuildRoleAssignmentScope(string subscriptionId, string? resourceGroupName, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            logger.LogWarning("Role assignment template resource did not include a scope. Deploying it at subscription scope.");
            return $"/subscriptions/{subscriptionId}";
        }

        var trimmedScope = scope.Trim();
        if (trimmedScope.StartsWith('/'))
            return trimmedScope;

        if (trimmedScope.StartsWith("subscriptions/", StringComparison.OrdinalIgnoreCase))
            return $"/{trimmedScope}";

        if (trimmedScope.StartsWith("resourceGroups/", StringComparison.OrdinalIgnoreCase))
            return $"/subscriptions/{subscriptionId}/{trimmedScope}";

        if (!trimmedScope.Contains('/'))
        {
            logger.LogWarning("Role assignment template resource scope could not be resolved. Deploying it at subscription scope.");
            return $"/subscriptions/{subscriptionId}";
        }

        return resourceGroupName == null
            ? $"/subscriptions/{subscriptionId}/providers/{trimmedScope.TrimStart('/')}"
            : $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/{trimmedScope.TrimStart('/')}";
    }

    private static (string? SubscriptionId, string? ResourceGroupName) GetDeploymentScope(string deploymentId)
    {
        var idParts = deploymentId.TrimStart('/').Split('/');
        var subscriptionId = idParts.Length > 1 && idParts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
            ? idParts[1]
            : null;
        var resourceGroupName = idParts.Length > 3 && idParts[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
            ? idParts[3]
            : null;

        return (subscriptionId, resourceGroupName);
    }

    private static string BuildSubscriptionScopeId(string subscriptionId, string type, string name) =>
        type.Equals("Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase)
            ? $"/subscriptions/{subscriptionId}/resourceGroups/{name}"
            : BuildResourceId($"/subscriptions/{subscriptionId}", type, name);

    private static string BuildResourceGroupScopeId(string subscriptionId, string resourceGroupName, string type, string name) =>
        BuildResourceId($"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}", type, name);

    private static string BuildResourceId(string scope, string type, string name)
    {
        var typeSegments = type.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var nameSegments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (typeSegments.Length == 0 || nameSegments.Length == 0)
            return $"{scope}/providers/{type}/{name}";

        var id = $"{scope}/providers/{typeSegments[0]}";
        for (var i = 1; i < typeSegments.Length; i++)
        {
            id += $"/{typeSegments[i]}";
            var nameIndex = i - 1;
            if (nameIndex < nameSegments.Length)
                id += $"/{nameSegments[nameIndex]}";
        }

        return id;
    }

    /// <summary>
    /// Extracts the evaluated output values from template outputs.
    /// After ProcessTemplateLanguageExpressions, the output values are evaluated in place.
    /// This method builds the outputs in the format { type, value } that Azure expects.
    /// </summary>
    private static Dictionary<string, object?> ExtractEvaluatedOutputs(InsensitiveDictionary<TemplateOutputParameter> outputs)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var output in outputs)
        {
            var extractedOutput = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            
            // Get type and value from TemplateOutputParameter
            // Both Type and Value are TemplateGenericProperty<T> wrappers
            if (output.Value.Type?.Value != null)
            {
                extractedOutput["type"] = output.Value.Type.Value.ToString().ToLowerInvariant();
            }
            else
            {
                extractedOutput["type"] = "object";
            }
            
            // The value is stored in output.Value.Value which is TemplateGenericProperty<JToken>
            // We need to extract the JToken from it
            var valueGenericProperty = output.Value.Value;
            if (valueGenericProperty != null)
            {
                // Try to get the actual value
                var jtoken = valueGenericProperty.Value;
                if (jtoken != null)
                {
                    // The JToken may contain a value at a nested property
                    // Check if it's structured as { value: {...}, type: {...}, ... }
                    if (jtoken is JObject jo && jo.TryGetValue("value", out var innerValue))
                    {
                        // Extract the inner value
                        extractedOutput["value"] = innerValue;
                    }
                    else
                    {
                        // Use the JToken directly
                        extractedOutput["value"] = jtoken;
                    }
                }
            }
            
            result[output.Key] = extractedOutput;
        }
        
        return result;
    }

    private void HandleNestedDeployment(
        GenericResource genericResource,
        TemplateDeployment parentDeployment,
        string evaluatedResourceJson,
        ref bool hasProvisioningFailed)
    {
        try
        {
            // Step 1: Parse raw resource JSON to extract nested template and context
            var resourceJson = JsonSerializer.Deserialize<JsonElement>(evaluatedResourceJson, GlobalSettings.JsonOptions);
            var resourceObj = resourceJson.Deserialize<Dictionary<string, JsonElement>>(GlobalSettings.JsonOptions);
            
            if (resourceObj == null)
            {
                logger.LogWarning($"Failed to parse nested deployment resource JSON for '{genericResource.Name}'.");
                hasProvisioningFailed = true;
                return;
            }

            var (_, parentResourceGroupName) = GetDeploymentScope(parentDeployment.Id);
            var nestedRgName = resourceObj.TryGetValue("resourceGroup", out var rgElement)
                ? rgElement.GetString()
                : parentResourceGroupName;

            if (string.IsNullOrWhiteSpace(nestedRgName))
            {
                logger.LogWarning($"Nested deployment '{genericResource.Name}' has no target resource group; subscription-scoped nested deployments must include a resourceGroup property.");
                return;
            }

            // Extract properties block
            if (!resourceObj.TryGetValue("properties", out var propsElement) || propsElement.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning($"Nested deployment '{genericResource.Name}' has no 'properties' object.");
                hasProvisioningFailed = true;
                return;
            }

            var propsObj = propsElement.Deserialize<Dictionary<string, JsonElement>>(GlobalSettings.JsonOptions);
            if (propsObj == null)
            {
                logger.LogWarning($"Failed to deserialize properties of nested deployment '{genericResource.Name}'.");
                hasProvisioningFailed = true;
                return;
            }

            // Extract inner template
            if (!propsObj.TryGetValue("template", out var templateElement))
            {
                logger.LogWarning($"Nested deployment '{genericResource.Name}' has no 'template' in properties.");
                hasProvisioningFailed = true;
                return;
            }

            var innerTemplateJson = templateElement.GetRawText();
            
            // Extract optional parameters and mode
            JsonElement? innerParams = propsObj.TryGetValue("parameters", out var paramsElement) 
                ? paramsElement 
                : (JsonElement?)null;
            var innerMode = propsObj.TryGetValue("mode", out var modeElement) 
                ? modeElement.GetString() ?? "Incremental" 
                : "Incremental";

            // Step 2: Resolve nested context identifiers
            var parentIdParts = parentDeployment.Id.TrimStart('/').Split('/');
            var nestedSubId = parentIdParts.Length > 1 && parentIdParts[0] == "subscriptions"
                ? SubscriptionIdentifier.From(parentIdParts[1])
                : throw new InvalidOperationException($"Cannot extract subscription ID from parent deployment ID: {parentDeployment.Id}");

            var nestedRgId = ResourceGroupIdentifier.From(nestedRgName);

            // Step 3: Build inner metadata
            var subscriptionMetadata = new SubscriptionMetadata(nestedSubId);
            
            // Extract parent RG metadata to get location
            var parentResourceGroupLocation = parentDeployment.Metadata.TryGetValue(DeploymentMetadata.ResourceGroupKey, out var rgMetadataToken)
                ? rgMetadataToken["location"]?.Value<string>()
                : null;

            AzureLocation nestedLocation;
            if (!string.IsNullOrWhiteSpace(genericResource.Location))
            {
                nestedLocation = new AzureLocation(genericResource.Location);
            }
            else if (!string.IsNullOrWhiteSpace(parentResourceGroupLocation))
            {
                nestedLocation = new AzureLocation(parentResourceGroupLocation);
            }
            else if (parentDeployment.Metadata.TryGetValue(DeploymentMetadata.LocationKey, out var parentLocationToken)
                     && parentLocationToken.Type == JTokenType.String
                     && !string.IsNullOrWhiteSpace(parentLocationToken.Value<string>()))
            {
                // Subscription-scoped parent deployments carry the location directly (no resource group metadata).
                nestedLocation = new AzureLocation(parentLocationToken.Value<string>()!);
            }
            else
            {
                logger.LogError(nameof(TemplateDeploymentOrchestrator), nameof(HandleNestedDeployment),
                    $"Nested deployment '{genericResource.Name}' has no location and parent location cannot be resolved.");
                hasProvisioningFailed = true;
                return;
            }

            var resourceGroupMetadata = new ResourceGroupMetadata(nestedSubId, nestedRgId, nestedLocation);

            var innerMetadataDict = new Dictionary<string, JToken>
            {
                { DeploymentMetadata.SubscriptionKey, JToken.Parse(subscriptionMetadata.ToString()) },
                { DeploymentMetadata.ResourceGroupKey, JToken.Parse(resourceGroupMetadata.ToString()) }
            };
            var innerMetadata = innerMetadataDict.ToInsensitiveDictionary(x => x.Key, x => x.Value);

            // Step 4: Parse and process inner template
            var innerTemplate = _armTemplateEngineFacade.Parse(innerTemplateJson);

            // Process template expressions first so Type/Name are resolved before ID assignment
            _armTemplateEngineFacade.ProcessTemplate(nestedSubId, nestedRgId, innerTemplate, innerMetadata, innerParams);

            // Assign resource IDs on inner template resources (after expression processing)
            foreach (var innerResource in innerTemplate.Resources)
            {
                innerResource.Id = new TemplateGenericProperty<string>
                {
                    Value = BuildResourceGroupScopeId(
                        nestedSubId.Value.ToString(),
                        nestedRgId.Value,
                        innerResource.Type.Value,
                        innerResource.Name.Value)
                };
            }

            // Step 5: Create and persist nested DeploymentResource
            var nestedDeploymentProps = DeploymentResourceProperties.New(innerMode, innerTemplateJson, null);
            var nestedDeploymentResource = new DeploymentResource(nestedSubId, nestedRgId, genericResource.Name, nestedLocation, nestedDeploymentProps);
            
            rgProvider.CreateOrUpdate(nestedSubId, nestedRgId, genericResource.Name, nestedDeploymentResource);

            logger.LogDebug(nameof(TemplateDeploymentOrchestrator), nameof(HandleNestedDeployment),
                "Created nested deployment resource '{0}'.", nestedDeploymentResource.Id);

            // Step 6: Build inner TemplateDeployment with linked cancellation
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentDeployment.CancellationToken);
            
            var innerJob = new TemplateDeployment(
                nestedDeploymentResource.Id,
                nestedDeploymentResource.Name,
                innerTemplate,
                complete: nestedDeploymentResource.CompleteDeployment,
                cancel: nestedDeploymentResource.CancelDeployment,
                fail: nestedDeploymentResource.FailDeployment,
                persist: () => rgProvider.CreateOrUpdate(nestedSubId, nestedRgId, genericResource.Name, nestedDeploymentResource),
                setOutputs: outputs => nestedDeploymentResource.Properties.Outputs = outputs,
                metadata: innerMetadata,
                parameters: innerParams,
                setError: error => nestedDeploymentResource.Properties.Error = error);

            innerJob.SetCancellationTokenSource(linkedCts);

            // Step 7: Recursive execution + status propagation
            try
            {
                RouteDeployment(innerJob);
            }
            catch (Exception ex)
            {
                logger.LogError(nameof(TemplateDeploymentOrchestrator), nameof(HandleNestedDeployment),
                    "Unhandled exception in nested deployment '{0}': {1}", genericResource.Name, ex.Message);
                nestedDeploymentResource.Properties.Error = new DeploymentErrorInfo { Code = "DeploymentFailed", Message = ex.Message };
                innerJob.Fail();
                innerJob.Persist();
                hasProvisioningFailed = true;
                linkedCts.Dispose();
                return;
            }

            if (innerJob.Status == TemplateDeployment.DeploymentStatus.Failed || 
                innerJob.Status == TemplateDeployment.DeploymentStatus.Cancelled)
            {
                hasProvisioningFailed = true;
            }

            linkedCts.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(nameof(TemplateDeploymentOrchestrator), nameof(HandleNestedDeployment),
                "Failed to handle nested deployment '{0}': {1}", genericResource.Name, ex.Message);
            hasProvisioningFailed = true;
        }
    }
}