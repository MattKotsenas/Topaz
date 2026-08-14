using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Topaz.EventPipeline;
using Topaz.CloudEnvironment.Models.Responses;
using Topaz.Identity;
using Topaz.Service.Authorization;
using Topaz.Service.Entra.Endpoints;
using Topaz.Service.Entra.Models.Responses;
using Topaz.Shared;

namespace Topaz.Tests;

public class EntraAuthorityTests
{
    private static readonly EntraAuthority ReservedAuthority =
        EntraAuthority.Create("https://entra.azure.test:8899");

    [Test]
    public void Configured_origin_drives_discovery_urls()
    {
        var discovery = new OpenIdConfigurationResponse(
            "50717675-3E5E-4A1E-8CB5-C62D8BE8CA48",
            ReservedAuthority);
        using var document = JsonDocument.Parse(discovery.ToString());
        var root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(
                root.GetProperty("issuer").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/50717675-3e5e-4a1e-8cb5-c62d8be8ca48/v2.0"));
            Assert.That(
                root.GetProperty("authorization_endpoint").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/organizations/oauth2/v2.0/authorize"));
            Assert.That(
                root.GetProperty("token_endpoint").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/organizations/oauth2/v2.0/token"));
            Assert.That(
                root.GetProperty("jwks_uri").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/common/discovery/v2.0/keys"));
            Assert.That(
                root.GetProperty("userinfo_endpoint").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/oidc/userinfo"));
            Assert.That(
                root.GetProperty("end_session_endpoint").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/common/oauth2/v2.0/logout"));
            Assert.That(
                root.GetProperty("device_authorization_endpoint").GetString(),
                Is.EqualTo("https://entra.azure.test:8899/organizations/oauth2/v2.0/devicecode"));
        });
    }

    [TestCase(
        "scope=https%3A%2F%2Fmanagement.azure.com%2F%2F.default",
        "https://management.azure.com/")]
    [TestCase(
        "scope=https%3A%2F%2Fmanagement.core.windows.net%2F%2F.default",
        "https://management.core.windows.net/")]
    [TestCase(
        "scope=https%3A%2F%2Fvault.azure.net%2F.default",
        "https://vault.azure.net")]
    public void Form_encoded_default_scope_maps_to_resource_audience(
        string body,
        string expectedAudience)
    {
        var form = TokenEndpoint.ParseFormBody(body);

        Assert.That(
            ReservedAuthority.GetAudience(form["scope"]),
            Is.EqualTo(expectedAudience));
    }

    [Test]
    public void Configured_authority_drives_token_issuer_and_explicit_audience()
    {
        var token = JwtHelper.GenerateJwt(
            Globals.GlobalAdminId,
            audience: "https://management.azure.com/",
            configuredAuthority: ReservedAuthority);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Multiple(() =>
        {
            Assert.That(jwt.Audiences.Single(), Is.EqualTo("https://management.azure.com/"));
            Assert.That(
                jwt.Claims.Single(claim => claim.Type == "tid").Value,
                Is.EqualTo("50717675-3e5e-4a1e-8cb5-c62d8be8ca48"));
            Assert.That(
                jwt.Issuer,
                Is.EqualTo(
                    "https://entra.azure.test:8899/50717675-3e5e-4a1e-8cb5-c62d8be8ca48/v2.0"));
        });
    }

    [TestCase(
        "50717675-3E5E-4A1E-8CB5-C62D8BE8CA48",
        "https://entra.azure.test:8899/50717675-3e5e-4a1e-8cb5-c62d8be8ca48/v2.0")]
    [TestCase(
        "50717675-3e5e-4a1e-8cb5-c62d8be8ca48",
        "https://entra.azure.test:8899/50717675-3e5e-4a1e-8cb5-c62d8be8ca48/v2.0")]
    [TestCase(
        "507176753e5e4a1e8cb5c62d8be8ca48",
        "https://entra.azure.test:8899/50717675-3e5e-4a1e-8cb5-c62d8be8ca48/v2.0")]
    [TestCase(
        "organizations",
        "https://entra.azure.test:8899/organizations/v2.0")]
    public void Tenant_issuer_canonicalizes_Guids_and_preserves_aliases(
        string tenant,
        string expectedIssuer)
    {
        Assert.That(ReservedAuthority.GetTenantIssuer(tenant), Is.EqualTo(expectedIssuer));
    }

    [Test]
    public void Managed_identity_token_issuer_and_tid_use_the_same_canonical_tenant()
    {
        var token = JwtHelper.CreateManagedIdentityToken(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "https://storage.azure.com/",
            "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi",
            "50717675-3E5E-4A1E-8CB5-C62D8BE8CA48");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var tenant = jwt.Claims.Single(claim => claim.Type == "tid").Value;

        Assert.Multiple(() =>
        {
            Assert.That(tenant, Is.EqualTo("50717675-3e5e-4a1e-8cb5-c62d8be8ca48"));
            Assert.That(
                jwt.Issuer,
                Is.EqualTo($"https://topaz.local.dev:8899/{tenant}/v2.0"));
        });
    }

    [Test]
    public void Configured_authority_drives_device_login_and_cloud_metadata()
    {
        var deviceCode = DeviceCodeResponse.Create("device-code", "USER-CODE", ReservedAuthority);
        var metadata = new GetMetadataEndpointResponse(ReservedAuthority);

        Assert.Multiple(() =>
        {
            Assert.That(
                deviceCode.VerificationUri,
                Is.EqualTo("https://entra.azure.test:8899/devicelogin"));
            Assert.That(
                deviceCode.Message,
                Does.Contain("https://entra.azure.test:8899/devicelogin"));
            Assert.That(metadata.ActiveDirectory, Is.EqualTo("https://entra.azure.test:8899"));
            Assert.That(metadata.Authentication.LoginEndpoint, Is.EqualTo("https://entra.azure.test:8899/"));
            Assert.That(metadata.ResourceManager, Is.EqualTo("https://topaz.local.dev:8899"));
            Assert.That(metadata.ActiveDirectoryGraphResourceId, Is.EqualTo("https://topaz.local.dev:8899"));
            Assert.That(metadata.MicrosoftGraphResourceId, Is.EqualTo("https://topaz.local.dev:8899/"));
        });
    }

    [Test]
    public void Token_response_uses_form_scope_for_access_and_refresh_audiences()
    {
        var context = new DefaultHttpContext();
        var form = TokenEndpoint.ParseFormBody(
            "scope=https%3A%2F%2Fmanagement.azure.com%2F%2F.default");
        var audience = TokenEndpoint.ResolveTokenAudience(context, form);
        var response = TokenEndpoint.CreateTokenResponse(
            context,
            Globals.GlobalAdminId,
            clientId: Globals.GlobalAdminId,
            storedNonce: null,
            username: null,
            form: form,
            audience: audience);

        var handler = new JwtSecurityTokenHandler();
        var accessToken = handler.ReadJwtToken(response.AccessToken);
        var refreshToken = handler.ReadJwtToken(response.RefreshToken);

        Assert.Multiple(() =>
        {
            Assert.That(accessToken.Audiences.Single(), Is.EqualTo("https://management.azure.com/"));
            Assert.That(refreshToken.Audiences.Single(), Is.EqualTo("https://management.azure.com/"));
        });
    }

    [TestCase("resource=https%3A%2F%2Ftopaz.local.dev%3A8899%2F.graph")]
    [TestCase("scope=https%3A%2F%2Fattacker.example%2F.graph%C2%AD%2F.default")]
    [TestCase("scope=https%3A%2F%2Fattacker.example%2F.graph%E2%80%8B%2F.default")]
    public void Caller_controlled_graph_audience_does_not_bypass_authorization(string body)
    {
        var context = new DefaultHttpContext();
        var form = TokenEndpoint.ParseFormBody(body);
        var audience = TokenEndpoint.ResolveTokenAudience(context, form);
        var principalId = Guid.NewGuid().ToString();
        var response = TokenEndpoint.CreateTokenResponse(
            context,
            principalId,
            clientId: principalId,
            storedNonce: null,
            username: null,
            form: form,
            audience: audience);
        var logger = new PrettyTopazLogger();
        var adapter = new AzureAuthorizationAdapter(new Pipeline(logger), logger);
        var scope = $"/subscriptions/{Guid.NewGuid()}/resourceGroups/rg";

        Assert.That(
            adapter.IsAuthorized(["Microsoft.Storage/storageAccounts/write"], response.AccessToken, scope).isAuthorized,
            Is.False);
    }

    [Test]
    public void Internal_graph_credential_retains_graph_authorization()
    {
        var logger = new PrettyTopazLogger();
        var adapter = new AzureAuthorizationAdapter(new Pipeline(logger), logger);
        var token = JwtHelper.GenerateJwt(Guid.NewGuid().ToString(), isForGraph: true);

        Assert.That(
            adapter.IsAuthorized(
                ["Microsoft.Graph/users/read"],
                token,
                $"/subscriptions/{Guid.NewGuid()}/resourceGroups/rg").isAuthorized,
            Is.True);
    }

    [Test]
    public void Default_authority_remains_the_legacy_topaz_origin()
    {
        Assert.That(
            EntraAuthority.Create(null).Origin,
            Is.EqualTo("https://topaz.local.dev:8899"));
    }

    [TestCase("http://entra.azure.test:8899")]
    [TestCase("https://entra.azure.test:8899/path")]
    [TestCase("https://user@entra.azure.test:8899")]
    [TestCase("https://entra.azure.test:8899?query=true")]
    [TestCase("https://entra.azure.test:8899#fragment")]
    public void Invalid_configured_origin_is_rejected(string value)
    {
        Assert.That(
            () => EntraAuthority.Create(value),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Network_path_endpoint_is_rejected()
    {
        Assert.That(
            () => ReservedAuthority.GetEndpoint("//other.example/path"),
            Throws.TypeOf<ArgumentException>());
    }
}
