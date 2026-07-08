using System.Security.Cryptography;
using System.Text;

using Microsoft.IdentityModel.Tokens;

namespace Topaz.Identity;

/// <summary>
/// The RSA signing key for Topaz-issued JWTs. A single fixed, well-known dev keypair — hardcoded, exactly
/// like the symmetric secret in <see cref="JwtHelper"/> — so every Topaz component agrees on it: the host,
/// the CLI, and in-process SDK credentials all sign and validate against the same key, even across
/// processes. The private key signs RS256 access tokens (matching the <c>RS256</c> that OIDC discovery
/// already advertises), and the public key is published verbatim by the JWKS endpoint
/// (<c>/common/discovery/v2.0/keys</c>) so standard clients — MSAL, the Azure SDKs, any JWKS-validating
/// resource server — can verify Topaz token signatures the real way. Real Entra rotates signing keys; the
/// emulator keeps one fixed well-known key, which is both sufficient and necessary for cross-process token
/// validation in a local emulator.
/// </summary>
public static class TopazSigningKey
{
    // A fixed, well-known RSA-2048 development keypair (PKCS#8 private key, base64). NOT a secret: this is
    // the asymmetric analog of JwtHelper's hardcoded symmetric dev key, present so a token minted in one
    // process (e.g. the Topaz CLI) validates in another (the host). A random per-process key would break
    // that. Rotate only if the well-known emulator key must change.
    private const string PrivateKeyPkcs8Base64 =
        "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDT2AOQuHbbp+pBkm4gL2fsLWn1+eRJpQAO94llhX5la+lh" +
        "SH7Teq+fBIAf+A6geZt8yiyko9RhrWF4RPzOwm0ev/SCCY0UkQKxC1kTTJsyoqqJGG9oeWQtsrsOHMcjD2koXQeHFqcy7E9tg" +
        "wGMzJ+37+PoSdMLU8mD+4Z8+qMgsw7hc1oWbwldZcuxE10RNfmMZDUeG1ymxeEJMXTQdoHouh9GlCDbl7siYh9NqG9H6z1N2N" +
        "iJnH1hyFAe0dRLvZQwYEWvh2bh+gfdFK23NxYU1Vrecv8rOQOoMONLCTO0+KS8yp5Bjis2B9xi1mJzAyEnmIi6QvyR4iFAtLl" +
        "9CwTlAgMBAAECggEAP3I2vyVAE9Fai4D7kpAgI9AGKDFLefL87X6dm9Y7YMzM/OHlehkIeCu04947IzzIoLs8W6LlfMucoZSn0" +
        "pTQcaEz7a5Gnp4/nB618t3CrYuiX6T92OBibH1XIIbl7U40RG54TrEuKkY0E6xkznKKc2BZdbyGhKH3fJvcT5oROT1bsW+Ucp" +
        "adEZSN8DU/71VEYqB53XWple1YgJpP4gISAIU6klXaffszrXiAEKdR5eY9G38vCsyKv6yD38EJAyywLabvxuK3b+c5I4Xrdb8" +
        "Na5oNJ0CrMW3Cgklh3ZYAC9TCPFmAlw/1/67yRSBBPqUbkxXZpOa4/TkQio+6rDIlkQKBgQD4jZmBr76z6bszzQCDrVE1SVJn" +
        "2UGGrXfqtBcXZlAGqestqiWXEuVYx9iChOY0c3Eg6i+lc8c1Eknzr1TESShFohAWE7oQMJPgO5UNqVgfKE1zoY10bCXVkf5FQ" +
        "6zR38kNhjAGUFhAMH+HOTAbClA9KzA9JTNTuqdst8xC9klbhwKBgQDaMNqnzV9RU+z11v1NC1hGiGSJoYL8EEYAKs7rkyF5Ep" +
        "PzcVVhnWzsffV7ES3gTGo2PkBAd5sXofwLnr42nQmaYZAGHAEdEA22YqVA2P1WrJOvFebQeIc1Lmf8UGawy3jCPUj8n6lAvf6" +
        "hLInDU/TbbXEnmdsmx91MGDduzmAvMwKBgAnNxAVKglIkYP7tEh0fg/l/F+ICvsPqKbW3PsXsgjGRGDan5G2uEB/NWivjxBTD" +
        "jO3IbvKuu2fLfeE/xC8t14nPl6TXSFqFIAATOZDdYh1wgIWUFLlH3sIqzQW8Yp+wnQSMi25kUubNQup5hf07Dekrv+5Zfkn" +
        "KLfpq0YK+piwXAoGBALLVBfIxBuXuprJccqI6ITE2S0jvAx+76tPqQkyc+/ty+aa3hmaKlCNFnfUvgG1t1EP/Q8RTA+Ab2Sx" +
        "hAMBcd+l7+4K2Y0dByCtrsMx0zTfEHQuNJPBLSW6SSZJpB7HyI1j4yCCecCfrUY8ipQtefbt3eR4fIZsohz3+Pzjnl7g3AoGA" +
        "PPvqE1RJ/ewO6cwQSnIZoh2sbQ4shFDYR3VYpPA2fdV+CaglG23cb2t4d35teNcAh+sDE6RgjiyixRIIdk27BVtDQ1jHO331I" +
        "etZArXwx+CUYHkqkn5TKROnklTtNOsRfyZhlqGZZfutuNH8eY4EmJmnQ6K23RgkueM7cEtM14o=";

    private static readonly RSA Rsa = CreateRsa();

    private static RSA CreateRsa()
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKeyPkcs8Base64), bytesRead: out _);
        return rsa;
    }

    /// <summary>The RSA security key (with a stable <c>kid</c>) used to sign tokens and to validate RS256 tokens.</summary>
    public static readonly RsaSecurityKey SecurityKey = new(Rsa) { KeyId = ComputeKid(Rsa) };

    /// <summary>The key id advertised in token headers and in the JWKS.</summary>
    public static string KeyId => SecurityKey.KeyId;

    /// <summary>
    /// The public key as a JWK (kty/n/e + kid/use/alg), for the JWKS endpoint. Exports the public
    /// parameters only — the private key never leaves the process.
    /// </summary>
    public static JsonWebKey PublicJwk()
    {
        var publicKey = new RsaSecurityKey(Rsa.ExportParameters(includePrivateParameters: false)) { KeyId = KeyId };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        return jwk;
    }

    // RFC 7638 JWK thumbprint over the RSA public key (the canonical members e, kty, n in lexical order).
    private static string ComputeKid(RSA rsa)
    {
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var canonical = $"{{\"e\":\"{e}\",\"kty\":\"RSA\",\"n\":\"{n}\"}}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64UrlEncoder.Encode(hash);
    }
}
