using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Topaz.Identity;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.Authorization.Endpoints;

/// <summary>
/// Shared responder for the ARM <c>Microsoft.Authorization/permissions</c> API at any scope. It returns the
/// caller's own effective permission blocks ("what can I do here"): it reads the caller's bearer token,
/// computes the effective set at the scope parsed from the request path, and writes the ARM
/// <c>{ "value": [ ... ] }</c> response. Any authenticated principal may read its own permissions, so no RBAC
/// permission gates the endpoint - but a valid token is required (the identity determines the result).
/// </summary>
internal static class PermissionsEndpointHelper
{
    private const string PermissionsSuffix = "/providers/Microsoft.Authorization/permissions";

    public static void WritePermissions(HttpContext context, HttpResponseMessage response,
        AzureAuthorizationAdapter adapter, ITopazLogger logger)
    {
        JwtSecurityToken? token;
        try
        {
            token = JwtHelper.ValidateJwt(context.Request.Headers["Authorization"].ToString());
        }
        catch (Exception e)
        {
            logger.LogDebug(nameof(PermissionsEndpointHelper), nameof(WritePermissions),
                "Rejecting a permissions request with an invalid or missing bearer token: {0}", e.Message);
            response.StatusCode = HttpStatusCode.Unauthorized;
            return;
        }

        if (token == null)
        {
            response.StatusCode = HttpStatusCode.Unauthorized;
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var suffixIndex = path.IndexOf(PermissionsSuffix, StringComparison.OrdinalIgnoreCase);
        var scope = suffixIndex >= 0 ? path[..suffixIndex] : path;

        var permissions = adapter.GetEffectivePermissions(token.Subject, scope);
        var value = permissions.Select(p => new
        {
            actions = p.Actions ?? [],
            notActions = p.NotActions ?? [],
            dataActions = p.DataActions ?? [],
            notDataActions = p.NotDataActions ?? []
        });

        response.StatusCode = HttpStatusCode.OK;
        response.Content = new StringContent(JsonSerializer.Serialize(new { value }, GlobalSettings.JsonOptions));
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }
}
