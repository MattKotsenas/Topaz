using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.IdentityModel.Tokens;

using Topaz.Identity;

namespace Topaz.Tests;

/// <summary>
/// Proves the OIDC signing contract Topaz advertises actually works: a Topaz-issued RS256 token
/// validates against the exact JWKS the <c>JwksEndpoint</c> publishes (<c>/common/discovery/v2.0/keys</c>),
/// using the standard Microsoft.IdentityModel validation an Azure SDK / resource server performs.
/// Before this, discovery advertised <c>RS256</c> + a <c>jwks_uri</c> but the JWKS 404'd and tokens were
/// HS256 — unverifiable by any standard client. The JWK JSON here is byte-identical in shape to the
/// endpoint's response (kty/use/kid/alg/n/e), so it exercises what a real client would fetch.
/// </summary>
public class EntraJwksTests
{
    /// <summary>Serialize the public JWK exactly as <c>JwksEndpoint.GetResponse</c> does.</summary>
    private static string PublishedJwksJson()
    {
        var jwk = TopazSigningKey.PublicJwk();
        var keySet = new
        {
            keys = new[]
            {
                new { kty = jwk.Kty, use = jwk.Use, kid = jwk.Kid, alg = jwk.Alg, n = jwk.N, e = jwk.E },
            },
        };
        return JsonSerializer.Serialize(keySet);
    }

    [Test]
    public void JWKS_publishes_the_RSA_public_key_with_a_stable_kid()
    {
        var jwks = new JsonWebKeySet(PublishedJwksJson());

        Assert.That(jwks.Keys, Has.Count.EqualTo(1), "the JWKS must publish exactly one signing key");
        var key = jwks.Keys[0];
        Assert.That(key.Kty, Is.EqualTo("RSA"));
        Assert.That(key.Use, Is.EqualTo("sig"));
        Assert.That(key.Alg, Is.EqualTo(SecurityAlgorithms.RsaSha256));
        Assert.That(key.Kid, Is.EqualTo(TopazSigningKey.KeyId));
        Assert.That(key.N, Is.Not.Empty, "modulus must be present");
        Assert.That(key.E, Is.Not.Empty, "exponent must be present");
    }

    [Test]
    public void The_signing_key_is_a_fixed_well_known_keypair_stable_across_processes()
    {
        // A random per-process RSA key (RSA.Create(2048)) would break cross-process validation: the Topaz
        // CLI mints a token in its own process and the host validates it in another. Pinning the kid (the
        // RFC-7638 thumbprint of the fixed dev public key) proves the key is the fixed well-known keypair.
        Assert.That(TopazSigningKey.KeyId, Is.EqualTo("t3PvkX0KdOn1FYhliZo9uMrBIJb6Vt0kxI2fwUGTp2Q"),
            "the RSA signing key must be the fixed well-known dev key so every Topaz process agrees on it");
    }

    [Test]
    public void Topaz_issued_RS256_token_validates_against_the_published_JWKS()
    {
        var jwks = new JsonWebKeySet(PublishedJwksJson());
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        var token = JwtHelper.GenerateCliToken();
        var handler = new JwtSecurityTokenHandler();

        // Precondition: the token really is RS256 signed with the published key id (how a client selects the key).
        var parsed = handler.ReadJwtToken(token);
        Assert.That(parsed.Header.Alg, Is.EqualTo(SecurityAlgorithms.RsaSha256),
            "precondition: Topaz must issue RS256, not the legacy HS256");
        Assert.That(parsed.Header.Kid, Is.EqualTo(TopazSigningKey.KeyId));

        // The load-bearing assertion: standard JWKS-based validation accepts the Topaz token.
        handler.ValidateToken(token, validationParameters, out var validated);
        Assert.That(((JwtSecurityToken)validated).Subject, Is.EqualTo(Globals.GlobalAdminId));
    }

    [Test]
    public void A_token_signed_by_an_unknown_key_is_rejected_by_the_published_JWKS()
    {
        var jwks = new JsonWebKeySet(PublishedJwksJson());
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        var handler = new JwtSecurityTokenHandler();

        Assert.That(() => handler.ValidateToken(SignWithForeignKey(), validationParameters, out _),
            Throws.InstanceOf<SecurityTokenException>(),
            "a token whose signing key is not in the JWKS must be rejected (this is the whole point of RBAC/authN)");
    }

    [Test]
    public void Id_token_is_RS256_and_validates_against_the_published_JWKS()
    {
        var idToken = JwtHelper.CreateIdToken(
            issuer: "https://topaz.local.dev:8899/organizations/v2.0",
            audience: "some-client-id",
            nonce: "n-123",
            userName: "user@topaz.local",
            objectId: Globals.GlobalAdminId,
            tenantId: "50717675-3E5E-4A1E-8CB5-C62D8BE8CA48");

        var handler = new JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(idToken);

        // Precondition: the id_token used to be emitted unsigned (alg=none); it must now be RS256 with the kid.
        Assert.That(parsed.Header.Alg, Is.EqualTo(SecurityAlgorithms.RsaSha256),
            "precondition: the id_token must be RS256-signed, not the legacy alg=none");
        Assert.That(parsed.Header.Kid, Is.EqualTo(TopazSigningKey.KeyId));

        var jwks = new JsonWebKeySet(PublishedJwksJson());
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        handler.ValidateToken(idToken, validationParameters, out var validated);
        Assert.That(((JwtSecurityToken)validated).Subject, Is.EqualTo(Globals.GlobalAdminId));
    }

    [Test]
    public void Access_token_iss_is_the_tenant_qualified_discovery_issuer()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(JwtHelper.GenerateCliToken());

        // The issuer OIDC discovery advertises for the tenant is BaseUrl/{tid}/v2.0; the access-token iss must
        // match it (it was previously the bare BaseUrl, which standard ValidateIssuer clients would reject).
        Assert.That(token.Issuer,
            Is.EqualTo("https://topaz.local.dev:8899/50717675-3E5E-4A1E-8CB5-C62D8BE8CA48/v2.0"));
    }

    [Test]
    public void Managed_identity_token_is_RS256_carries_the_MI_claims_and_validates_against_the_JWKS()
    {
        const string principalId = "11111111-1111-1111-1111-111111111111";
        const string clientId = "22222222-2222-2222-2222-222222222222";
        const string resource = "https://storage.azure.com/";
        const string mirid =
            "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/mi";

        var token = JwtHelper.CreateManagedIdentityToken(principalId, clientId, resource, mirid);

        var jwks = new JsonWebKeySet(PublishedJwksJson());
        var handler = new JwtSecurityTokenHandler();

        // The MI token must verify against the published JWKS just like any other Topaz-issued token.
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        }, out var validated);

        var jwt = (JwtSecurityToken)validated;
        Assert.That(jwt.Header.Alg, Is.EqualTo(SecurityAlgorithms.RsaSha256),
            "the MI token must be RS256 so Topaz is the single JWKS-verifiable issuer");
        // The RBAC principal is the MI's principal id; the client id and mirid identify the identity.
        Assert.That(jwt.Subject, Is.EqualTo(principalId));
        Assert.That(jwt.Claims.First(c => c.Type == "oid").Value, Is.EqualTo(principalId));
        Assert.That(jwt.Claims.First(c => c.Type == "appid").Value, Is.EqualTo(clientId));
        Assert.That(jwt.Claims.First(c => c.Type == "azp").Value, Is.EqualTo(clientId));
        Assert.That(jwt.Claims.First(c => c.Type == "xms_mirid").Value, Is.EqualTo(mirid));
        Assert.That(jwt.Audiences, Does.Contain(resource));
    }

    private static string SignWithForeignKey()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "unknown-foreign-key" };
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "intruder"), new Claim("oid", "intruder")]),
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    // The shared HMAC key Topaz historically signed with, still accepted as a validation fallback.
    // External HS256 issuers that share this well-known key model legacy tokens that must keep validating
    // through the RS256 switch.
    private static readonly byte[] LegacySharedKey =
        "yD1sMV1WcwVjSfNUxxLNfVHn5sbqD056LwOnkXCkIDnWkXcrg95plLQ3T1tvinLAnuNNiRRZrKyUvs6YzZnJ/A=="u8.ToArray();

    private static string SignLegacyHs256(string objectId, byte[] key)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", objectId), new Claim("oid", objectId)]),
            Issuer = "https://topaz.local.dev:8899",
            Audience = "https://topaz.local.dev:8899",
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    [Test]
    public void Legacy_HS256_token_with_the_shared_key_still_validates_through_ValidateJwt()
    {
        var token = SignLegacyHs256("legacy-external-principal", LegacySharedKey);

        // Precondition: this really is a legacy HS256 token (not RS256), so it can only pass via the fallback key.
        var alg = new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Alg;
        Assert.That(alg, Is.EqualTo(SecurityAlgorithms.HmacSha256),
            "precondition: the token must be HS256 to exercise the legacy fallback, not the RS256 primary path");

        var validated = JwtHelper.ValidateJwt(token);
        Assert.That(validated, Is.Not.Null);
        Assert.That(validated!.Subject, Is.EqualTo("legacy-external-principal"),
            "the RS256 switch must not break legacy HS256 tokens signed with the shared key");
    }

    [Test]
    public void Topaz_RS256_token_validates_through_the_production_ValidateJwt()
    {
        var token = JwtHelper.GenerateCliToken();

        // Precondition: Topaz now issues RS256, so this exercises the primary RSA path in the production validator.
        var alg = new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Alg;
        Assert.That(alg, Is.EqualTo(SecurityAlgorithms.RsaSha256),
            "precondition: Topaz must issue RS256 after the switch");

        var validated = JwtHelper.ValidateJwt(token);
        Assert.That(validated, Is.Not.Null);
        Assert.That(validated!.Subject, Is.EqualTo(Globals.GlobalAdminId));
    }

    [Test]
    public void HS256_token_signed_with_a_foreign_key_is_rejected_by_ValidateJwt()
    {
        // A different 64-byte HMAC key — the fallback must actually verify the signature, not accept any HS256 token.
        var foreignKey = new byte[64];
        RandomNumberGenerator.Fill(foreignKey);
        Assert.That(foreignKey, Is.Not.EqualTo(LegacySharedKey),
            "precondition: the foreign key must differ from the shared fallback key");

        var token = SignLegacyHs256("intruder", foreignKey);

        Assert.That(() => JwtHelper.ValidateJwt(token),
            Throws.InstanceOf<SecurityTokenException>(),
            "an HS256 token not signed with the shared key must be rejected — the fallback still checks the signature");
    }
}
