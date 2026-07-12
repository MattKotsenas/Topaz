using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Topaz.Identity;
using Topaz.ResourceManager;

namespace Topaz.Tests.E2E;

public class NestedDeploymentTests
{
    private static readonly ArmClientOptions ArmClientOptions = TopazArmClientOptions.New;

    private static string GetNestedDeploymentTemplate() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", "deployment-nested.json"));

    [Test]
    public async Task NestedDeployment_SubscriptionScopeWithInlineTemplate_InnerResourcesProvisioned()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var credentials = new AzureLocalCredential(Globals.GlobalAdminId);
        var armClient = new ArmClient(credentials, subscriptionId.ToString(), ArmClientOptions);
        using var topaz = new TopazArmClient(credentials);
        await topaz.CreateSubscriptionAsync(subscriptionId, "nested-deploy-sub");
        
        var subscription = await armClient.GetDefaultSubscriptionAsync();
        
        // Act: Deploy the subscription-scoped template
        await subscription.GetArmDeployments().CreateOrUpdateAsync(
            WaitUntil.Completed,
            "sub-deploy-with-nested",
            new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(GetNestedDeploymentTemplate())
            })
            {
                Location = AzureLocation.WestEurope
            });

        subscription = await armClient.GetDefaultSubscriptionAsync();

        // Assert: Verify the resource group was created
        var targetRgName = "nested-kv-rg";
        ResourceGroupResource? targetRg = null;
        try
        {
            targetRg = await subscription.GetResourceGroups().GetAsync(targetRgName);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            Assert.Fail($"Target resource group '{targetRgName}' was not created.");
        }

        // Assert: Verify the Key Vault was created inside the target resource group
        KeyVaultResource? keyVault = null;
        try
        {
            var kvCollection = targetRg.GetKeyVaults();
            await foreach (var kv in kvCollection.GetAllAsync())
            {
                if (kv.Data.Name.Contains("nestedkvtest"))
                {
                    keyVault = kv;
                    break;
                }
            }
        }
        catch
        {
            // Expected if no KeyVaults exist
        }

        Assert.That(keyVault, Is.Not.Null, "Expected Key Vault 'nestedkvtest' to exist in the target resource group.");

        // Assert: Verify the nested deployment resource exists and has succeeded
        var nestedDeploymentName = "nested-kv-deploy";
        ArmDeploymentResource? nestedDeployment = null;
        try
        {
            nestedDeployment = await targetRg.GetArmDeploymentAsync(nestedDeploymentName);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            Assert.Fail($"Nested deployment resource '{nestedDeploymentName}' was not created.");
        }

        Assert.That(nestedDeployment, Is.Not.Null);
        Assert.That(nestedDeployment.Data.Properties.ProvisioningState?.ToString(), Is.EqualTo("Succeeded").IgnoreCase,
            "Expected nested deployment to have succeeded.");
    }

    // A resource-group-scoped template whose nested Microsoft.Resources/deployments OMITS
    // the 'resourceGroup' property. Per the ARM spec, the nested deployment then inherits
    // the parent deployment's resource group and creates its resources there.
    private static string BuildRgScopedInheritedNestedTemplate(string vaultName) => $$"""
        {
          "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
          "contentVersion": "1.0.0.0",
          "resources": [
            {
              "type": "Microsoft.Resources/deployments",
              "apiVersion": "2021-04-01",
              "name": "inner-kv-deploy",
              "properties": {
                "mode": "Incremental",
                "expressionEvaluationOptions": { "scope": "inner" },
                "template": {
                  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                  "contentVersion": "1.0.0.0",
                  "resources": [
                    {
                      "type": "Microsoft.KeyVault/vaults",
                      "apiVersion": "2022-07-01",
                      "name": "{{vaultName}}",
                      "location": "westeurope",
                      "properties": {
                        "enabledForTemplateDeployment": true,
                        "tenantId": "00000000-0000-0000-0000-000000000000",
                        "sku": { "name": "standard", "family": "A" },
                        "accessPolicies": [],
                        "networkAcls": { "defaultAction": "Allow", "bypass": "AzureServices" }
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
    public async Task NestedDeployment_RgScopeWithoutResourceGroup_InheritsParentRgAndProvisionsInner()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        const string resourceGroupName = "nested-inherit-rg";
        const string vaultName = "nestedinheritkv";
        var credentials = new AzureLocalCredential(Globals.GlobalAdminId);
        var armClient = new ArmClient(credentials, subscriptionId.ToString(), ArmClientOptions);
        using var topaz = new TopazArmClient(credentials);
        await topaz.CreateSubscriptionAsync(subscriptionId, "nested-inherit-sub");

        var subscription = await armClient.GetDefaultSubscriptionAsync();
        var rg = await subscription.GetResourceGroups().CreateOrUpdateAsync(
            WaitUntil.Completed, resourceGroupName, new ResourceGroupData(AzureLocation.WestEurope));

        // Act: an RG-scoped deployment whose nested deployment omits 'resourceGroup'.
        await rg.Value.GetArmDeployments().CreateOrUpdateAsync(
            WaitUntil.Completed, "outer-with-inherited-nested",
            new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(BuildRgScopedInheritedNestedTemplate(vaultName))
            }));

        // Assert: both the nested deployment resource and its Key Vault live in the parent's resource group.
        var nested = await rg.Value.GetArmDeploymentAsync("inner-kv-deploy");
        Assert.That(nested.Value.Data.Properties.ProvisioningState?.ToString(),
            Is.EqualTo("Succeeded").IgnoreCase, "The inherited nested deployment should have succeeded.");

        KeyVaultResource? keyVault = null;
        try
        {
            keyVault = await rg.Value.GetKeyVaults().GetAsync(vaultName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Assert.Fail($"Key Vault '{vaultName}' was not created in the inherited resource group '{resourceGroupName}'.");
        }

        Assert.That(keyVault, Is.Not.Null);
    }
}
