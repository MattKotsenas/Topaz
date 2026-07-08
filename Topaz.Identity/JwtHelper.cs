using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Topaz.Identity;

public static class JwtHelper
{
    private const string TenantId = "50717675-3E5E-4A1E-8CB5-C62D8BE8CA48";

    private static readonly byte[] SecretKey =
        "yD1sMV1WcwVjSfNUxxLNfVHn5sbqD056LwOnkXCkIDnWkXcrg95plLQ3T1tvinLAnuNNiRRZrKyUvs6YzZnJ/A=="u8.ToArray();

    /// <summary>
    /// Generates a short-lived JWT token for the Topaz CLI to authenticate with the Topaz Host.
    /// The token is issued as the global admin principal, bypassing RBAC checks.
    /// </summary>
    public static string GenerateCliToken() => GenerateJwt(Globals.GlobalAdminId);

    internal static string GenerateJwt(string objectId, bool isForGraph = false, string? preferredUsername = null)
    {
        return CreateJwt(objectId, isForGraph ? "https://topaz.local.dev:8899/.graph" : "https://topaz.local.dev:8899",
            preferredUsername);
    }

    /// <summary>
    /// Issues a short-lived ACR token (refresh or access) signed with the same key as all other Topaz JWTs.
    /// </summary>
    public static string IssueAcrToken(string objectId)
    {
        return CreateJwt(objectId, "https://topaz.local.dev:8899");
    }

    private static string CreateJwt(string objectId, string audience, string? preferredUsername = null)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new("sub", objectId),
            new("oid", objectId),
            new("appid", objectId),
            new("azp", objectId),
            new("tid", TenantId)
        };

        if (!string.IsNullOrWhiteSpace(preferredUsername))
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            // Tenant-qualified issuer matching what OIDC discovery advertises (BaseUrl/{tid}/v2.0), so a
            // standard client validating the issuer against discovery accepts the token.
            Issuer = $"https://topaz.local.dev:8899/{TenantId}/v2.0",
            Audience = audience,
            NotBefore = DateTime.UtcNow,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(TopazSigningKey.SecurityKey, SecurityAlgorithms.RsaSha256)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);
        return tokenString;
    }

    /// <summary>
    /// Issues an RS256-signed OpenID Connect <c>id_token</c> (previously emitted unsigned as
    /// <c>alg=none</c>). Signed with the same key as every other Topaz token so discovery's advertised
    /// <c>id_token_signing_alg_values_supported: ["RS256"]</c> holds and standard OIDC clients can verify it
    /// against the JWKS.
    /// </summary>
    public static string CreateIdToken(string issuer, string audience, string? nonce, string userName,
        string objectId, string tenantId)
    {
        var claims = new List<Claim>
        {
            new("oid", objectId),
            new("sub", objectId),
            new("tid", tenantId),
            new("preferred_username", userName)
        };

        if (!string.IsNullOrEmpty(nonce))
        {
            claims.Add(new Claim("nonce", nonce));
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = audience,
            NotBefore = DateTime.UtcNow,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(TopazSigningKey.SecurityKey, SecurityAlgorithms.RsaSha256)
        };
        return tokenHandler.WriteToken(tokenHandler.CreateToken(tokenDescriptor));
    }
    
    public static JwtSecurityToken? ValidateJwt(string jwt)
    {
        jwt = NormalizeBearerToken(jwt);
        
        var tokenHandler = new JwtSecurityTokenHandler();

        tokenHandler.ValidateToken(jwt, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            // Topaz now signs RS256 (validated via the RSA public key, the same key the JWKS publishes).
            // The symmetric key is retained as a fallback so legacy HS256 tokens (issued by any external
            // component that shares Topaz's well-known HMAC key) keep validating during the transition to
            // RSA-signed tokens as the single issuer.
            IssuerSigningKeys = [TopazSigningKey.SecurityKey, new SymmetricSecurityKey(SecretKey)],
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        }, out var validatedToken);

        return (JwtSecurityToken)validatedToken;
    }
    
    private static string NormalizeBearerToken(string? token)
    {
        token = token?.Trim();

        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        const string bearerPrefix = "Bearer ";
        if (token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            token = token[bearerPrefix.Length..].Trim();

        // Common copy/paste / serialization artifacts
        token = token.Trim().Trim('"');

        return token;
    }
}