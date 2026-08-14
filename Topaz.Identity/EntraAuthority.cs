using Topaz.Shared;

namespace Topaz.Identity;

internal sealed class EntraAuthority
{
    private const string EnvironmentVariable = "TOPAZ_ENTRA_AUTHORITY";
    private const string DefaultScopeSuffix = "/.default";

    private static EntraAuthority? _current;
    private readonly Uri _origin;

    private EntraAuthority(Uri origin)
    {
        _origin = origin;
    }

    public static EntraAuthority Current =>
        _current ??= Create(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public string Origin => _origin.GetLeftPart(UriPartial.Authority);

    public string OrganizationsIssuer => GetTenantIssuer("organizations");

    public static EntraAuthority Create(string? configuredOrigin)
    {
        var value = string.IsNullOrWhiteSpace(configuredOrigin)
            ? GlobalSettings.DefaultResourceManagerOrigin
            : configuredOrigin;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps
            || origin.AbsolutePath != "/"
            || origin.UserInfo.Length > 0
            || origin.Query.Length > 0
            || origin.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must be an absolute HTTPS origin.");
        }

        return new EntraAuthority(origin);
    }

    public string GetTenantIssuer(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return $"{Origin}/{tenantId}/v2.0";
    }

    public string GetEndpoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Endpoint path must be root-relative.", nameof(path));
        }

        return new Uri(_origin, path).AbsoluteUri;
    }

    public string GetAudience(string? scope)
    {
        // OAuth 2.0 static scopes identify the resource before the trailing `/.default`.
        if (scope is not null)
        {
            foreach (var value in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (value.EndsWith(DefaultScopeSuffix, StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(value, UriKind.Absolute, out _))
                {
                    var resource = value[..^DefaultScopeSuffix.Length];
                    if (string.Equals(
                            resource,
                            "https://management.azure.com",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return "https://management.azure.com/";
                    }

                    if (string.Equals(
                            resource,
                            "https://management.core.windows.net",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return "https://management.core.windows.net/";
                    }

                    return resource;
                }
            }
        }

        return Origin;
    }
}
