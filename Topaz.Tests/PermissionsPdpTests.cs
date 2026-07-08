using Topaz.EventPipeline;
using Topaz.Identity;
using Topaz.Service.Authorization;
using Topaz.Service.Authorization.Domain;
using Topaz.Service.Authorization.Models.Requests;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Subscription;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// In-process coverage for the ARM <c>Microsoft.Authorization/permissions</c> PDP
/// (<see cref="AzureAuthorizationAdapter.GetEffectivePermissions"/>): given a principal's role assignments, it
/// returns the effective permission blocks at a scope. Drives the real control-plane file store (no HTTP host),
/// so it runs anywhere the solution builds.
/// </summary>
public sealed class PermissionsPdpTests
{
    // Built-in "Storage Blob Data Contributor" - the role a keyless blob writer needs.
    private const string StorageBlobDataContributorId =
        "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string BlobWriteDataAction =
        "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write";

    [Test]
    public void Effective_permissions_include_the_assigned_role_data_actions_at_the_resource_scope()
    {
        var logger = new SilentLogger();
        var pipeline = new Pipeline(logger);
        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var principalId = Guid.NewGuid().ToString();
        var accountScope = AccountScope(subscription);

        try
        {
            SeedAssignment(pipeline, logger, subscription, principalId, StorageBlobDataContributorId, accountScope);
            var adapter = new AzureAuthorizationAdapter(pipeline, logger);

            // Precondition: a principal with no assignment has nothing here (proves the grant below is real).
            Assert.That(adapter.GetEffectivePermissions(Guid.NewGuid().ToString(), accountScope), Is.Empty,
                "precondition: an unassigned principal must have no effective permissions");

            var granted = adapter.GetEffectivePermissions(principalId, accountScope)
                .SelectMany(p => p.DataActions ?? []).ToArray();

            Assert.That(granted, Does.Contain(BlobWriteDataAction),
                "the assigned Storage Blob Data Contributor role grants blob write at the account scope");
        }
        finally
        {
            Cleanup(subscription);
        }
    }

    [Test]
    public void An_account_scoped_assignment_does_not_grant_at_the_parent_resource_group_scope()
    {
        var logger = new SilentLogger();
        var pipeline = new Pipeline(logger);
        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var principalId = Guid.NewGuid().ToString();
        var resourceGroupScope = $"/subscriptions/{subscription.Value}/resourceGroups/rg";
        var accountScope = $"{resourceGroupScope}/providers/Microsoft.Storage/storageAccounts/acct";

        try
        {
            // Assign ONLY at the narrower account scope.
            SeedAssignment(pipeline, logger, subscription, principalId, StorageBlobDataContributorId, accountScope);
            var adapter = new AzureAuthorizationAdapter(pipeline, logger);

            // Precondition: the assignment does grant at the account scope (so the negative below is meaningful).
            Assert.That(adapter.GetEffectivePermissions(principalId, accountScope), Is.Not.Empty,
                "precondition: the account-scoped assignment must grant at the account scope");

            // Scope propagation is directional: an account-scoped grant must not surface at the parent RG.
            Assert.That(adapter.GetEffectivePermissions(principalId, resourceGroupScope), Is.Empty,
                "an assignment scoped to the account must not propagate up to the resource group");
        }
        finally
        {
            Cleanup(subscription);
        }
    }

    [Test]
    public void The_global_admin_has_unrestricted_effective_permissions()
    {
        var logger = new SilentLogger();
        var pipeline = new Pipeline(logger);
        var scope = AccountScope(SubscriptionIdentifier.From(Guid.NewGuid()));

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
        var principalId = Guid.NewGuid().ToString();
        Assert.That(principalId, Is.Not.EqualTo(Globals.GlobalAdminId), "precondition: not the global-admin bypass");

        var permissions = new AzureAuthorizationAdapter(pipeline, logger)
            .GetEffectivePermissions(principalId, "/providers/Microsoft.Management/managementGroups/mg-root");

        Assert.That(permissions, Is.Empty);
    }

    private static string AccountScope(SubscriptionIdentifier subscription) =>
        $"/subscriptions/{subscription.Value}/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";

    private static void SeedAssignment(Pipeline pipeline, ITopazLogger logger, SubscriptionIdentifier subscription,
        string principalId, string roleDefinitionId, string scope)
    {
        SubscriptionControlPlane.New(pipeline, logger).Create(subscription, "pdp-test", null);

        AuthorizationControlPlane.New(pipeline, logger).CreateOrUpdateRoleAssignment(
            subscription,
            RoleAssignmentName.From(Guid.NewGuid()),
            new CreateOrUpdateRoleAssignmentRequest
            {
                Properties = new RoleAssignmentProperties
                {
                    RoleDefinitionId = roleDefinitionId,
                    PrincipalId = principalId,
                    Scope = scope
                }
            },
            scope);
    }

    private static void Cleanup(SubscriptionIdentifier subscription)
    {
        var root = Path.Combine(GlobalSettings.MainEmulatorDirectory, ".subscription", subscription.Value.ToString());
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
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
