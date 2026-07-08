namespace Topaz.Identity;

public class Globals
{
    public const string GlobalAdminId = "00000000-0000-0000-0000-000000000000";

    // Well-known bootstrap service principal seeded at startup. External tools can authenticate as
    // this principal via the OAuth2 client_credentials grant (BootstrapClientId + BootstrapClientSecret)
    // to obtain a real bearer token whose access is governed by real RBAC - it is granted the built-in
    // Owner role at the Tenant Root Group, so it can perform privileged operations without relying on the
    // in-process global-admin bypass. Intended for local/development use only.
    public const string BootstrapClientId = "11111111-1111-1111-1111-111111111111";
    public const string BootstrapClientSecret = "topaz-bootstrap-secret";
    public const string BootstrapPrincipalObjectId = "22222222-2222-2222-2222-222222222222";
    public const string BootstrapOwnerRoleAssignmentName = "33333333-3333-3333-3333-333333333333";
}