using Topaz.Service.Entra.Models;
using Topaz.Service.ManagedIdentity.Models.Requests;
using Topaz.Shared;

namespace Topaz.Service.ManagedIdentity.Models;

public sealed class ManagedIdentityResourceProperties
{
    public string? ClientId { get; set; }
    public string? PrincipalId { get; set; }
    public string? TenantId { get; set; }
    public string? IsolationScope { get; set; }

    internal static ManagedIdentityResourceProperties From(
        CreateUpdateManagedIdentityRequest.ManagedIdentityProperties? properties, ServicePrincipal servicePrincipal)
    {
        return new ManagedIdentityResourceProperties
        {
            ClientId = servicePrincipal.AppId,
            PrincipalId = servicePrincipal.Id,
            // A managed identity belongs to the emulator's (single) tenant - matching the tid the token
            // endpoint stamps - so the resource and its issued tokens agree on the tenant.
            TenantId = GlobalSettings.DefaultTenantId,
            IsolationScope = properties?.IsolationScope
        };
    }
}