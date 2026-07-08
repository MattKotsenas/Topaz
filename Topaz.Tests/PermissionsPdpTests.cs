using Topaz.EventPipeline;
using Topaz.Identity;
using Topaz.Service.Authorization;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// In-process coverage for the ARM <c>Microsoft.Authorization/permissions</c> PDP
/// (<see cref="AzureAuthorizationAdapter.GetEffectivePermissions"/>). Only the platform-independent branches
/// are covered here: the assignment-gathering path reads the file store via
/// <c>ResourceProviderBase.List</c>, which filters by a forward-slash path split calibrated to the Linux
/// container's path depth, so it cannot be exercised in-process on Windows. That path reuses the exact
/// <c>ListSubscriptionRoleAssignmentsByEntraObject</c> the proven <c>IsAuthorized</c> uses, and is validated by
/// the containerized end-to-end suite.
/// </summary>
public sealed class PermissionsPdpTests
{
    [Test]
    public void The_global_admin_has_unrestricted_effective_permissions()
    {
        var logger = new SilentLogger();
        var pipeline = new Pipeline(logger);
        var scope = $"/subscriptions/{Guid.NewGuid()}/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";

        var permissions = new AzureAuthorizationAdapter(pipeline, logger)
            .GetEffectivePermissions(Globals.GlobalAdminId, scope);

        Assert.That(permissions.SelectMany(p => p.Actions ?? []), Does.Contain("*"));
        Assert.That(permissions.SelectMany(p => p.DataActions ?? []), Does.Contain("*"),
            "the global admin bypasses RBAC, so its effective data actions are unrestricted");
    }

    [Test]
    public void A_scope_outside_any_subscription_yields_no_effective_permissions()
    {
        var logger = new SilentLogger();
        var pipeline = new Pipeline(logger);
        var adapter = new AzureAuthorizationAdapter(pipeline, logger);

        // Precondition: a non-admin principal (so the global-admin bypass does not apply) ...
        var principalId = Guid.NewGuid().ToString();
        Assert.That(principalId, Is.Not.EqualTo(Globals.GlobalAdminId));

        // ... querying a management-group scope with no subscription returns nothing (the guard branch).
        var permissions = adapter.GetEffectivePermissions(principalId,
            "/providers/Microsoft.Management/managementGroups/mg-root");

        Assert.That(permissions, Is.Empty);
    }

#pragma warning disable CS0618 // implementing the obsolete LogDebug(string) overload for the no-op logger
    private sealed class SilentLogger : ITopazLogger
    {
        public LogLevel LogLevel => LogLevel.Error;
        public void LogInformation(string message) { }
        public void LogDebug(string message) { }
        public void LogDebug(string methodName, string message) { }
        public void LogDebug(string className, string methodName, params object[] parameters) { }
        public void LogDebug(string className, string methodName, string template, params object?[] parameters) { }
        public void LogError(Exception ex) { }
        public void LogError(string message) { }
        public void LogError(string className, string methodName, string template, params object?[] parameters) { }
        public void LogWarning(string message) { }
        public void SetLoggingLevel(LogLevel level) { }
        public void EnableLoggingToFile(bool refreshLog) { }
        public void ConfigureIdFactory(CorrelationIdFactory idFactory) { }
    }
#pragma warning restore CS0618
}
