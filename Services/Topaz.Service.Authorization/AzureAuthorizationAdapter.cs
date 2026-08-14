using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Topaz.EventPipeline;
using Topaz.Identity;
using Topaz.Service.Authorization.Domain;
using Topaz.Service.Authorization.Models;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Shared;
using Topaz.Shared.Extensions;

namespace Topaz.Service.Authorization;

public sealed class AzureAuthorizationAdapter(Pipeline eventPipeline, ITopazLogger logger) : IArmAuthorizationChecker
{
    private readonly AuthorizationControlPlane _controlPlane = AuthorizationControlPlane.New(eventPipeline, logger);
    
    public (bool isAuthorized, ClaimsPrincipal? principal) IsAuthorized(string[] requiredPermissions, string token, string? scope)
    {
        logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
            "Attempting to check authorization for {0} token against {1} permissions...", token,
            requiredPermissions.Length);
        
        if (requiredPermissions.Length == 0)
        {
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "No permissions defined for the endpoint. It's treated as a public endpoint.");
            return (true, null);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized), "No token provided.");
            return (false, null);
        }

        if (token.StartsWith("SharedKey") || token.StartsWith("SharedAccessSignature"))
        {
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "Shared key token - skipping authorization as it's offloaded to the service.");
            return (true, null);
        }
        
        var validatedToken = JwtHelper.ValidateJwt(token);
        if (validatedToken == null)
        {
            logger.LogError(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized), "Invalid token.");
            return (false, null);
        }

        logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
            "Token validated - attempting authorization for `{0}`", validatedToken.Subject);

        if (validatedToken.Claims.Any(
                claim => claim.Type == JwtHelper.TokenTypeClaim
                    && string.Equals(
                        claim.Value,
                        JwtHelper.GraphTokenType,
                        StringComparison.Ordinal)))
        {
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "Token is for Microsoft Graph - skipping authorization. This behaviour may change in the future.");
            return (true, GetClaimsPrincipal(validatedToken));
        }

        if (validatedToken.Subject == Globals.GlobalAdminId)
        {
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "Token is for global admin - skipping authorization.");
            return (true, GetClaimsPrincipal(validatedToken)); 
        }

        if (scope == null || !scope.Contains("/subscriptions/"))
        {
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "Scope does not contain `/subscriptions/` - skipping authorization.");
            return (true, GetClaimsPrincipal(validatedToken));
        }
        
        var subscriptionIdentifier = SubscriptionIdentifier.From(scope.ExtractValueFromPath(2));
        var assignments =
            _controlPlane.ListSubscriptionRoleAssignmentsByEntraObject(subscriptionIdentifier, validatedToken.Subject);

        // Only consider assignments whose scope is a prefix of the requested scope (hierarchy propagation).
        var applicableAssignments = (assignments.Resource ?? [])
            .Where(a => !string.IsNullOrEmpty(a.Properties.Scope) &&
                        scope.StartsWith(a.Properties.Scope, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var assignment in applicableAssignments)
        {
            var definition = _controlPlane.Get(subscriptionIdentifier,
                RoleDefinitionIdentifier.From(assignment.Properties.RoleDefinitionId));
            
            if (definition.Resource == null)
            {
                logger.LogError(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                    "Failed to retrieve role definition for assignment: `{0}`, reason: `{1}`", assignment.Id, definition.Reason);
                continue;
            }

            if (!PermissionChecks.HasAnyRequiredPermission(definition.Resource.Properties.Permissions,
                    requiredPermissions)) continue;
            
            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "Found required permissions in role definition: {0}", definition.Resource.Id);
            return (true, GetClaimsPrincipal(validatedToken));
        }

        // Fall back to management-group-level assignments (MG scope propagates down through
        // the hierarchy relationship, not through scope string prefix matching).
        var mgAssignments = _controlPlane.ListManagementGroupRoleAssignmentsByEntraObject(
            subscriptionIdentifier.Value.ToString(), validatedToken.Subject);

        foreach (var mgAssignment in mgAssignments)
        {
            var definition = _controlPlane.Get(subscriptionIdentifier,
                RoleDefinitionIdentifier.From(mgAssignment.Properties.RoleDefinitionId));

            if (definition.Resource == null)
            {
                logger.LogError(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                    "Failed to retrieve role definition for MG assignment: `{0}`", mgAssignment.Id);
                continue;
            }

            if (!PermissionChecks.HasAnyRequiredPermission(definition.Resource.Properties.Permissions,
                    requiredPermissions)) continue;

            logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
                "Found required permissions via management group role definition: {0}", definition.Resource.Id);
            return (true, GetClaimsPrincipal(validatedToken));
        }

        logger.LogDebug(nameof(AzureAuthorizationAdapter), nameof(IsAuthorized),
            "No required permissions found in any role definition for the given subscription and object ID.");
        return (false, null);
    }

    /// <summary>
    /// Computes the caller's effective permission blocks at <paramref name="scope"/>: the permission entries
    /// (actions/notActions/dataActions/notDataActions) from every role definition the principal is assigned at
    /// or above the scope - subscription assignments matched by scope prefix, plus management-group assignments
    /// that propagate through the hierarchy. This is what the ARM <c>Microsoft.Authorization/permissions</c> API
    /// returns ("what can I do here"); a caller unions the actions and subtracts the not-actions to decide access.
    /// </summary>
    internal IReadOnlyList<RoleDefinition.Permission> GetEffectivePermissions(string objectId, string? scope)
    {
        // The global admin bypasses RBAC everywhere (mirrors IsAuthorized); report unrestricted access.
        if (objectId == Globals.GlobalAdminId)
        {
            return [new RoleDefinition.Permission { Actions = ["*"], DataActions = ["*"], NotActions = [], NotDataActions = [] }];
        }

        if (string.IsNullOrWhiteSpace(scope) || !scope.Contains("/subscriptions/"))
        {
            return [];
        }

        var subscriptionIdentifier = SubscriptionIdentifier.From(scope.ExtractValueFromPath(2));

        var subscriptionRoleDefinitionIds = (_controlPlane
                .ListSubscriptionRoleAssignmentsByEntraObject(subscriptionIdentifier, objectId).Resource ?? [])
            .Where(a => !string.IsNullOrEmpty(a.Properties.Scope) &&
                        scope.StartsWith(a.Properties.Scope, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Properties.RoleDefinitionId);

        var managementGroupRoleDefinitionIds = _controlPlane
            .ListManagementGroupRoleAssignmentsByEntraObject(subscriptionIdentifier.Value.ToString(), objectId)
            .Select(a => a.Properties.RoleDefinitionId);

        var permissions = new List<RoleDefinition.Permission>();
        var seenDefinitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var roleDefinitionId in subscriptionRoleDefinitionIds.Concat(managementGroupRoleDefinitionIds))
        {
            if (string.IsNullOrEmpty(roleDefinitionId)) continue;

            var definition = _controlPlane.Get(subscriptionIdentifier, RoleDefinitionIdentifier.From(roleDefinitionId));
            if (definition.Resource?.Properties.Permissions == null) continue;
            // A principal can hold the same role via several assignments; report each definition's permissions once.
            if (definition.Resource.Id != null && !seenDefinitions.Add(definition.Resource.Id)) continue;

            permissions.AddRange(definition.Resource.Properties.Permissions);
        }

        return permissions;
    }

    public bool PrincipalHasPermissions(SubscriptionIdentifier subscriptionIdentifier, string objectId,
        string[] requiredPermissions)
    {
        var assignments =
            _controlPlane.ListSubscriptionRoleAssignmentsByEntraObject(subscriptionIdentifier, objectId);
        if (assignments.Resource == null || assignments.Resource.Length == 0) return false;

        foreach (var assignment in assignments.Resource)
        {
            var definition = _controlPlane.Get(subscriptionIdentifier,
                RoleDefinitionIdentifier.From(assignment.Properties.RoleDefinitionId));
            if (definition.Resource == null) continue;
            if (PermissionChecks.HasAnyRequiredPermission(definition.Resource.Properties.Permissions,
                    requiredPermissions)) return true;
        }

        return false;
    }

    private static ClaimsPrincipal GetClaimsPrincipal(JwtSecurityToken validatedToken)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, validatedToken.Subject)
        ]));
    }
}