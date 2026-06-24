using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Extensions;
using Newtonsoft.Json.Linq;
using Azure.Core;
using Azure.Deployments.Core.Entities;
using System.Reflection;
using Topaz.EventPipeline;
using Topaz.Service.ResourceManager;
using Topaz.Service.ResourceManager.Deployment;
using Topaz.Service.ResourceManager.Models;
using Topaz.Service.ResourceGroup;
using Topaz.Service.ResourceGroup.Models;
using Topaz.Service.ManagedIdentity;
using Topaz.Service.ManagedIdentity.Models;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Subscription;
using Topaz.Shared;
using DeploymentResource = Topaz.Service.ResourceManager.Models.DeploymentResource;
using ResourceManagerDeploymentMetadata = Topaz.Service.ResourceManager.Deployment.DeploymentMetadata;

namespace Topaz.Tests.E2E;

public class ArmDeploymentResourceEvaluationTests
{
    private sealed class CapturingLogger : ITopazLogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public LogLevel LogLevel => LogLevel.Warning;
        public void LogInformation(string message) { }
        public void LogDebug(string message) { }
        public void LogDebug(string methodName, string message) { }
        public void LogDebug(string className, string methodName, params object[] parameters) { }
        public void LogDebug(string className, string methodName, string template, params object?[] parameters) { }
        public void LogError(Exception ex) => Errors.Add(ex.ToString());
        public void LogError(string message) => Errors.Add(message);
        public void LogError(string className, string methodName, string template, params object?[] parameters) =>
            Errors.Add(string.Format(template, parameters));
        public void LogWarning(string message) => Warnings.Add(message);
        public void SetLoggingLevel(LogLevel level) { }
        public void EnableLoggingToFile(bool refreshLog) { }
        public void ConfigureIdFactory(CorrelationIdFactory idFactory) { }
    }

    private static string BuildRoleAssignmentTemplate(Guid roleDefinitionId) => $$"""
        {
          "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
          "contentVersion": "1.0.0.0",
          "resources": [
            {
              "type": "Microsoft.Authorization/roleAssignments",
              "apiVersion": "2022-04-01",
              "name": "11111111-1111-1111-1111-111111111111",
              "properties": {
                "literalValue": "/literal/survives",
                "roleDefinitionId": "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '{{roleDefinitionId}}')]"
              }
            }
          ]
        }
        """;

    private static string BuildZeroCountCopyTemplate() => """
        {
          "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
          "contentVersion": "1.0.0.0",
          "parameters": {
            "identityNames": {
              "type": "array",
              "defaultValue": []
            }
          },
          "resources": [
            {
              "copy": {
                "name": "identityCopy",
                "count": "[length(parameters('identityNames'))]"
              },
              "type": "Microsoft.ManagedIdentity/userAssignedIdentities",
              "apiVersion": "2023-01-31",
              "name": "[parameters('identityNames')[copyIndex()]]",
              "location": "westeurope"
            },
            {
              "type": "Microsoft.ManagedIdentity/userAssignedIdentities",
              "apiVersion": "2023-01-31",
              "name": "copyzero-normal",
              "location": "westeurope"
            }
          ]
        }
        """;

    private static string BuildNestedFederatedCredentialTemplate(string identityName, string credentialName) => $$"""
        {
          "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
          "contentVersion": "1.0.0.0",
          "variables": {
            "identityName": "{{identityName}}",
            "credentialName": "{{credentialName}}"
          },
          "resources": [
            {
              "type": "Microsoft.ManagedIdentity/userAssignedIdentities",
              "apiVersion": "2023-01-31",
              "name": "[variables('identityName')]",
              "location": "westeurope"
            },
            {
              "type": "Microsoft.Resources/deployments",
              "apiVersion": "2022-09-01",
              "name": "nested-federated-credential",
              "dependsOn": [
                "[resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', variables('identityName'))]"
              ],
              "properties": {
                "mode": "Incremental",
                "expressionEvaluationOptions": {
                  "scope": "inner"
                },
                "parameters": {
                  "identityName": {
                    "value": "[variables('identityName')]"
                  },
                  "credentialName": {
                    "value": "[variables('credentialName')]"
                  }
                },
                "template": {
                  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                  "contentVersion": "1.0.0.0",
                  "parameters": {
                    "identityName": {
                      "type": "string"
                    },
                    "credentialName": {
                      "type": "string"
                    }
                  },
                  "resources": [
                    {
                      "type": "Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials",
                      "apiVersion": "2023-01-31",
                      "name": "[format('{0}/{1}', parameters('identityName'), parameters('credentialName'))]",
                      "properties": {
                        "issuer": "https://token.actions.githubusercontent.com",
                        "subject": "repo:example/repository:ref:refs/heads/main",
                        "audiences": [
                          "api://AzureADTokenExchange"
                        ]
                      }
                    }
                  ]
                }
              }
            }
          ]
        }
        """;

    [Test]
    public void EvaluateResource_WhenPropertyContainsSubscriptionResourceId_ReturnsPlainJsonValue()
    {
        var subscriptionId = Guid.Parse("00000000-0000-0000-0000-000000000000");
        var roleDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var expectedRoleDefinitionId =
            $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{roleDefinitionId}";

        var facade = new ArmTemplateEngineFacade();
        var template = facade.Parse(BuildRoleAssignmentTemplate(roleDefinitionId));
        facade.ProcessTemplate(
            new SubscriptionIdentifier(subscriptionId),
            new ResourceGroupIdentifier("rg"),
            template,
            InsensitiveDictionary<JToken>.Empty,
            null);

        var resource = template.Resources.Single();
        Assert.That(resource.ToJson(), Does.Contain("subscriptionResourceId"),
            "precondition: generic resource properties are not evaluated by the template preprocessing pass.");

        var logger = new CapturingLogger();
        var evaluatedResource = facade.EvaluateResource(
            subscriptionId.ToString(),
            "rg",
            template,
            resource,
            logger);

        var properties = (JObject)evaluatedResource["properties"]!;
        Assert.That(properties["literalValue"]!.Value<string>(), Is.EqualTo("/literal/survives"));
        Assert.That(properties["roleDefinitionId"]!.Value<string>(), Is.EqualTo(expectedRoleDefinitionId),
            string.Join(Environment.NewLine, logger.Warnings));
    }

    [Test]
    public void ProcessTemplate_WhenResourceCopyCountResolvesToZero_RemovesCopiedResource()
    {
        var facade = new ArmTemplateEngineFacade();
        var template = facade.Parse(BuildZeroCountCopyTemplate());

        facade.ProcessTemplate(
            new SubscriptionIdentifier(Guid.Parse("00000000-0000-0000-0000-000000000000")),
            new ResourceGroupIdentifier("rg"),
            template,
            InsensitiveDictionary<JToken>.Empty,
            null);

        Assert.That(template.Resources, Has.Length.EqualTo(1));
        Assert.That(template.Resources.Single().Name.Value, Is.EqualTo("copyzero-normal"));
    }

    [Test]
    public void ResourceGroupDeployment_WhenResourceCopyCountResolvesToZero_PersistsNormalResource()
    {
        var logger = new CapturingLogger();
        var pipeline = new Pipeline(logger);
        var subscriptionId = new SubscriptionIdentifier(Guid.NewGuid());
        var resourceGroupId = new ResourceGroupIdentifier($"rg-copy-zero-{Guid.NewGuid():N}"[..24]);
        var deployment = ExecuteResourceGroupDeployment(
            subscriptionId,
            resourceGroupId,
            "copy-count-zero",
            BuildZeroCountCopyTemplate(),
            logger,
            pipeline);

        var provider = new ManagedIdentityResourceProvider(logger);
        var normalIdentity = provider.GetAs<ManagedIdentityResource>(
            subscriptionId,
            resourceGroupId,
            "copyzero-normal");

        Assert.Multiple(() =>
        {
            Assert.That(deployment.Properties.ProvisioningState, Is.EqualTo("Succeeded"));
            Assert.That(normalIdentity, Is.Not.Null);
        });
    }

    [Test]
    public void ResourceGroupNestedDeployment_WhenInnerTemplateContainsFederatedCredential_PersistsCredential()
    {
        var logger = new CapturingLogger();
        var pipeline = new Pipeline(logger);
        var subscriptionId = new SubscriptionIdentifier(Guid.NewGuid());
        var resourceGroupId = new ResourceGroupIdentifier($"rg-nested-fic-{Guid.NewGuid():N}"[..24]);
        var identityName = "nested-fic-identity";
        var credentialName = "nested-fic";
        var deployment = ExecuteResourceGroupDeployment(
            subscriptionId,
            resourceGroupId,
            "nested-fic",
            BuildNestedFederatedCredentialTemplate(identityName, credentialName),
            logger,
            pipeline);

        var credential = new ManagedIdentityResourceProvider(logger)
            .GetSubresourceAs<FederatedIdentityCredentialResource>(
                subscriptionId,
                resourceGroupId,
                credentialName,
                identityName,
                "federatedIdentityCredentials");

        Assert.Multiple(() =>
        {
            Assert.That(deployment.Properties.ProvisioningState, Is.EqualTo("Succeeded"),
                string.Join(Environment.NewLine, logger.Errors));
            Assert.That(credential, Is.Not.Null);
            Assert.That(credential!.Properties.Issuer, Is.EqualTo("https://token.actions.githubusercontent.com"));
            Assert.That(credential.Properties.Subject, Is.EqualTo("repo:example/repository:ref:refs/heads/main"));
            Assert.That(credential.Properties.Audiences, Does.Contain("api://AzureADTokenExchange"));
        });
    }

    private static DeploymentResource ExecuteResourceGroupDeployment(
        SubscriptionIdentifier subscriptionId,
        ResourceGroupIdentifier resourceGroupId,
        string deploymentName,
        string templateJson,
        ITopazLogger logger,
        Pipeline pipeline)
    {
        SubscriptionControlPlane.New(pipeline, logger).Create(subscriptionId, "arm-template-test", null);
        new ResourceGroupResourceProvider(logger).CreateOrUpdate(
            subscriptionId,
            resourceGroupId,
            null,
            new ResourceGroupResource(
                subscriptionId,
                resourceGroupId.Value,
                new AzureLocation("westeurope"),
                new ResourceGroupProperties()));

        var facade = new ArmTemplateEngineFacade();
        var template = facade.Parse(templateJson);
        var metadata = new ResourceManagerDeploymentMetadata
        {
            { ResourceManagerDeploymentMetadata.SubscriptionKey, JToken.Parse(new SubscriptionMetadata(subscriptionId).ToString()) },
            { ResourceManagerDeploymentMetadata.ResourceGroupKey, JToken.Parse(new ResourceGroupMetadata(subscriptionId, resourceGroupId, new AzureLocation("westeurope")).ToString()) }
        }.ToInsensitiveDictionary(x => x.Key, x => x.Value);

        facade.ProcessTemplate(subscriptionId, resourceGroupId, template, metadata, null);
        foreach (var resource in template.Resources)
        {
            resource.Id = new TemplateGenericProperty<string>
            {
                Value = BuildResourceId(subscriptionId.Value.ToString(), resourceGroupId.Value, resource.Type.Value, resource.Name.Value)
            };
        }

        var deployment = new DeploymentResource(
            subscriptionId,
            resourceGroupId,
            deploymentName,
            new AzureLocation("westeurope"),
            DeploymentResourceProperties.New("Incremental", templateJson, null));
        var resourceProvider = new ResourceManagerResourceProvider(logger);
        var orchestrator = new TemplateDeploymentOrchestrator(
            pipeline,
            resourceProvider,
            new SubscriptionDeploymentResourceProvider(logger),
            new TenantDeploymentResourceProvider(logger),
            new ManagementGroupDeploymentResourceProvider(logger),
            logger);
        var job = new TemplateDeployment(
            deployment.Id,
            deployment.Name,
            template,
            deployment.CompleteDeployment,
            deployment.CancelDeployment,
            deployment.FailDeployment,
            () => resourceProvider.CreateOrUpdate(subscriptionId, resourceGroupId, deployment.Name, deployment),
            outputs => deployment.Properties.Outputs = outputs,
            metadata,
            deployment.Properties.Parameters,
            error => deployment.Properties.Error = error);

        typeof(TemplateDeploymentOrchestrator)
            .GetMethod("RouteDeployment", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(orchestrator, [job]);

        return deployment;
    }

    private static string BuildResourceId(string subscriptionId, string resourceGroupName, string type, string name)
    {
        var typeSegments = type.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var nameSegments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/{typeSegments[0]}";
        for (var i = 1; i < typeSegments.Length; i++)
        {
            id += $"/{typeSegments[i]}";
            var nameIndex = i - 1;
            if (nameIndex < nameSegments.Length)
                id += $"/{nameSegments[nameIndex]}";
        }

        return id;
    }
}
