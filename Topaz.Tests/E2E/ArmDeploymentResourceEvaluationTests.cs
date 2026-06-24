using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Extensions;
using Newtonsoft.Json.Linq;
using Topaz.Service.ResourceManager;
using Topaz.Service.Shared.Domain;
using Topaz.Shared;

namespace Topaz.Tests.E2E;

public class ArmDeploymentResourceEvaluationTests
{
    private sealed class CapturingLogger : ITopazLogger
    {
        public List<string> Warnings { get; } = [];
        public LogLevel LogLevel => LogLevel.Warning;
        public void LogInformation(string message) { }
        public void LogDebug(string message) { }
        public void LogDebug(string methodName, string message) { }
        public void LogDebug(string className, string methodName, params object[] parameters) { }
        public void LogDebug(string className, string methodName, string template, params object?[] parameters) { }
        public void LogError(Exception ex) { }
        public void LogError(string message) { }
        public void LogError(string className, string methodName, string template, params object?[] parameters) { }
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
}
