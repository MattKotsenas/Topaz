using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Web;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Topaz.Identity;
using Topaz.Service.Entra.Domain;
using Topaz.Service.Entra.Models.Responses;
using Topaz.Service.Entra.Planes;
using Topaz.Service.Shared;
using Topaz.Shared;
using Topaz.Shared.Extensions;

namespace Topaz.Service.Entra.Endpoints;

public class TokenEndpoint(ITopazLogger logger) : IEndpointDefinition
{
    private static readonly ConcurrentDictionary<string, string> RefreshTokenUsernames = new();

    private readonly UserDataPlane _userDataPlane = UserDataPlane.New(logger);
    private readonly ServicePrincipalDataPlane _servicePrincipalDataPlane = ServicePrincipalDataPlane.New(logger);
    private readonly ApplicationsDataPlane _applicationsDataPlane = ApplicationsDataPlane.New(logger);

    public string[] Endpoints =>
    [
        "POST /organizations/oauth2/v2.0/token",
        // MSAL refreshes tokens via tenant-specific and /common paths, not just /organizations.
        "POST /{tenantId}/oauth2/v2.0/token",
        "POST /common/oauth2/v2.0/token",
        // Old go-autorest (Azure SDK v1) used by azurerm provider for Key Vault data-plane calls
        // issues token requests against the v1.0 endpoint without the /v2.0 segment.
        "POST /organizations/oauth2/token",
        "POST /{tenantId}/oauth2/token",
        "POST /common/oauth2/token",
    ];

    public string[] Permissions => [];

    public (ushort[] Ports, Protocol Protocol) PortsAndProtocol =>
        ([GlobalSettings.DefaultResourceManagerPort], Protocol.Https);

    public void GetResponse(HttpContext context, HttpResponseMessage response, GlobalOptions options)
    {
        // Build a minimal unsigned JWT (header.payload.) so MSAL can decode the id_token payload.

        // Parse POST form body to obtain the authorization 'code'.
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = reader.ReadToEnd();
        }

        logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse), "Received body: {0}", body);

        string? code = null;
        var form = ParseFormBody(body);
        form.TryGetValue("code", out code);

        logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse), "Received code: {0}", code);

        var clientId = ExtractValueForToken(context, form, "client_id", Guid.NewGuid().ToString());
        var username = ExtractValueForToken(context, form, "username", null);
        var grantType = ExtractValueForToken(context, form, "grant_type", null);

        logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse), "Received client_id: {0}", clientId);
        logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse), "Received username: {0}", username);
        logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse), "Received grant type: {0}", grantType);

        // Look up and remove stored nonce for this code if present.
        AuthorizeEndpoint.Nonces.TryRemove(code ?? string.Empty, out var storedNonce);

        // If username isn't empty we can look for the user to check their ID
        string? objectId = null;
        if (!string.IsNullOrEmpty(username) && grantType == "password")
        {
            logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse), "Looking up user `{0}`...", username);

            var userOperation = _userDataPlane.Get(UserIdentifier.From(username));
            if (userOperation.Resource == null || userOperation.Result != OperationResult.Success)
            {
                response.CreateJsonContentResponse(ErrorResponse.Create(ErrorResponse.InvalidClient, "Invalid user."),
                    HttpStatusCode.BadRequest);
                return;
            }

            var savedPassword = userOperation.Resource.PasswordProfile?.Password;
            var password = ExtractValueForToken(context, form, "password", null);

            if (savedPassword == null || !string.Equals(savedPassword, password, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(nameof(TokenEndpoint), nameof(GetResponse), "Invalid password.");
                response.CreateJsonContentResponse(
                    ErrorResponse.Create(ErrorResponse.InvalidClient, "Invalid password."),
                    HttpStatusCode.BadRequest);
                return;
            }

            objectId = userOperation.Resource.Id;
        }

        switch (grantType)
        {
            case "refresh_token":
            {
                logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse),
                    "Extracting object ID from refresh token...");

                var refreshToken = ExtractValueForToken(context, form, "refresh_token", null);
                if (refreshToken == null)
                {
                    logger.LogError(nameof(TokenEndpoint), nameof(GetResponse), "Could not extract refresh token.");
                    response.CreateJsonContentResponse(
                        ErrorResponse.Create(ErrorResponse.InvalidRequest, "Could not extract refresh token."),
                        HttpStatusCode.BadRequest);
                    return;
                }

                try
                {
                    var validatedToken = JwtHelper.ValidateJwt(refreshToken);
                    if (validatedToken == null)
                    {
                        logger.LogError(nameof(TokenEndpoint), nameof(GetResponse), "Could not validate refresh token.");
                        response.CreateJsonContentResponse(
                            ErrorResponse.Create(ErrorResponse.InvalidClient, "Could not validate refresh token."),
                            HttpStatusCode.Unauthorized);
                        return;
                    }

                    objectId = validatedToken.Subject;

                    var tokenUsername = validatedToken.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value;
                    if (!string.IsNullOrWhiteSpace(tokenUsername))
                    {
                        username = tokenUsername;
                    }

                    // Prefer the original username associated with this refresh token.
                    // Az.Accounts expects preferred_username to remain stable across refreshes.
                    if (RefreshTokenUsernames.TryGetValue(refreshToken, out var cachedUsername))
                    {
                        username = cachedUsername;
                    }
                }
                catch (SecurityTokenExpiredException ex)
                {
                    logger.LogError(nameof(TokenEndpoint), nameof(GetResponse), $"Refresh token expired - {ex.Message}");
                    response.CreateJsonContentResponse(
                        ErrorResponse.Create(ErrorResponse.InvalidRequest, "Refresh token expired"),
                        HttpStatusCode.BadRequest);
                    return;
                }

                // Rehydrate username for MSAL/Az.Accounts token cache matching.
                // If preferred_username is replaced with object ID on refresh, cmdlets
                // can fail looking up the cached account by UPN.
                if (objectId != null && string.IsNullOrWhiteSpace(username))
                {
                    var userOperation = _userDataPlane.Get(UserIdentifier.From(objectId));
                    if (userOperation.Resource != null && userOperation.Result == OperationResult.Success)
                    {
                        username = userOperation.Resource.UserPrincipalName;
                    }
                }
                
                break;
            }
            case "authorization_code":
            {
                logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse),
                    "Extracting object ID from authorization code...");

                // Retrieve the login_hint stored when the code was issued, then remove it.
                if (AuthorizeEndpoint.AuthCodes.TryRemove(code ?? string.Empty, out var loginHint) &&
                    !string.IsNullOrWhiteSpace(loginHint))
                {
                    var userOperation = _userDataPlane.Get(UserIdentifier.From(loginHint));
                    if (userOperation.Resource == null || userOperation.Result != OperationResult.Success)
                    {
                        logger.LogError(nameof(TokenEndpoint), nameof(GetResponse),
                            "Could not find user for login_hint: {0}", loginHint);
                        response.CreateJsonContentResponse(
                            ErrorResponse.Create(ErrorResponse.InvalidClient, "Could not find user."),
                            HttpStatusCode.BadRequest);
                        return;
                    }

                    objectId = userOperation.Resource.Id;
                    username = userOperation.Resource.UserPrincipalName;
                }
                else
                {
                    // No login_hint was captured during authorize — fall back to global admin
                    // so that browser-based flows that omit login_hint continue to work in the emulator.
                    objectId = Globals.GlobalAdminId;
                    var userOperation = _userDataPlane.Get(UserIdentifier.From(objectId));
                    if (userOperation.Resource != null && userOperation.Result == OperationResult.Success)
                    {
                        username = userOperation.Resource.UserPrincipalName;
                    }
                }

                break;
            }
            case "client_credentials":
            {
                logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse),
                    "client_credentials grant — validating service principal credentials.");

                var clientSecret = ExtractValueForToken(context, form, "client_secret", null);

                // Look up the service principal by appId (= client_id).
                var spOperation = _servicePrincipalDataPlane.Get(ServicePrincipalIdentifier.From(clientId!));
                if (spOperation.Resource == null || spOperation.Result != OperationResult.Success)
                {
                    logger.LogError(nameof(TokenEndpoint), nameof(GetResponse),
                        "Service principal not found for client_id: {0}", clientId);
                    response.CreateJsonContentResponse(
                        ErrorResponse.Create(ErrorResponse.InvalidClient, "Invalid client credentials."),
                        HttpStatusCode.Unauthorized);
                    return;
                }

                // Validate the client_secret against the application's stored password credentials.
                var appOperation = _applicationsDataPlane.Get(ApplicationIdentifier.From(clientId!), includePasswords: true);
                var validSecret = appOperation.Resource?.PasswordCredentials
                    ?.Any(pc => string.Equals(pc.SecretText, clientSecret, StringComparison.Ordinal)) == true;

                if (!validSecret)
                {
                    logger.LogError(nameof(TokenEndpoint), nameof(GetResponse),
                        "Invalid client_secret for client_id: {0}", clientId);
                    response.CreateJsonContentResponse(
                        ErrorResponse.Create(ErrorResponse.InvalidClient, "Invalid client credentials."),
                        HttpStatusCode.Unauthorized);
                    return;
                }

                objectId = spOperation.Resource.Id;
                username = spOperation.Resource.AppId; // az account show reports user.name as the appId
                break;
            }
            case "urn:ietf:params:oauth:grant-type:device_code":
            {
                logger.LogDebug(nameof(TokenEndpoint), nameof(GetResponse),
                    "device_code grant — resolving device code.");

                var deviceCode = ExtractValueForToken(context, form, "device_code", null);
                if (deviceCode == null ||
                    !DeviceCodeEndpoint.AuthorizedDeviceCodes.TryRemove(deviceCode, out var deviceObjectId))
                {
                    response.CreateJsonContentResponse(
                        ErrorResponse.Create("authorization_pending",
                            "The device has not been authorized yet. Please wait and try again."),
                        HttpStatusCode.BadRequest);
                    return;
                }

                objectId = deviceObjectId;
                var deviceUserOperation = _userDataPlane.Get(UserIdentifier.From(objectId));
                if (deviceUserOperation.Resource != null &&
                    deviceUserOperation.Result == OperationResult.Success)
                {
                    username = deviceUserOperation.Resource.UserPrincipalName;
                }

                break;
            }
        }

        if (objectId == null)
        {
            logger.LogError(nameof(TokenEndpoint), nameof(GetResponse), "Could not extract object ID from request.");
            response.CreateJsonContentResponse(
                ErrorResponse.Create(ErrorResponse.InvalidRequest, "Could not extract object ID from request."),
                HttpStatusCode.BadRequest);
            return;
        }

        var audience = ResolveTokenAudience(context, form);

        var token = CreateTokenResponse(
            context,
            objectId,
            clientId,
            storedNonce,
            username,
            form,
            audience);

        if (!string.IsNullOrWhiteSpace(token.RefreshToken) && !string.IsNullOrWhiteSpace(username))
        {
            RefreshTokenUsernames[token.RefreshToken] = username;
        }

        response.CreateJsonContentResponse(token);
    }

    internal static TokenResponse CreateTokenResponse(HttpContext context, string objectId, string? clientId,
        string? storedNonce,
        string? username, Dictionary<string, string> form, string audience)
    {
        {
            var authority = EntraAuthority.Current;
            var scope = ExtractValueForToken(context, form, "scope", null);
            var credential = new AzureLocalCredential(
                objectId,
                isForGraph: false,
                preferredUsername: username,
                audience: audience);
            var token = new TokenResponse
            {
                AccessToken = credential
                    .GetToken(new TokenRequestContext(), CancellationToken.None).Token,
                RefreshToken = credential
                    .GetToken(new TokenRequestContext(), CancellationToken.None).Token,
                IdToken = JwtHelper.CreateIdToken(
                    authority.OrganizationsIssuer,
                    clientId!,
                    storedNonce,
                    username ?? objectId,
                    objectId,
                    EntraService.TenantId),
                Scope = scope ?? "openid profile offline_access"
            };
            return token;
        }
    }

    internal static string ResolveTokenAudience(
        HttpContext context,
        Dictionary<string, string> form)
    {
        var resource = ExtractValueForToken(context, form, "resource", null);
        return string.IsNullOrWhiteSpace(resource)
            ? EntraAuthority.Current.GetAudience(ExtractValueForToken(context, form, "scope", null))
            : resource;
    }

    private static string? ExtractValueForToken(HttpContext context, Dictionary<string, string> form, string key,
        string? defaultValue)
    {
        return form.TryGetValue(key, out var cid) ? cid :
            context.Request.Query.TryGetValueForKey(key, out var qcid) ? qcid :
            defaultValue;
    }

    internal static Dictionary<string, string> ParseFormBody(string body)
    {
        var parsed = HttpUtility.ParseQueryString(body);
        return parsed.AllKeys
            .Where(key => key is not null)
            .ToDictionary(
                key => key!,
                key => parsed[key] ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

}