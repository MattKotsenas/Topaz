using System.Globalization;

namespace Topaz.Shared;

internal sealed class ContainerRegistryAuthority
{
    private const string EnvironmentVariable =
        "TOPAZ_CONTAINER_REGISTRY_LOGIN_SERVER_AUTHORITY_SUFFIX";

    private static ContainerRegistryAuthority? _current;
    private readonly string _loginServerSuffix;

    private ContainerRegistryAuthority(
        string dnsSuffix,
        int? port,
        bool preserveLegacyTagMetadataAuthority)
    {
        DnsSuffix = dnsSuffix;
        _loginServerSuffix = port is null ? dnsSuffix : $"{dnsSuffix}:{port}";
        PreserveLegacyTagMetadataAuthority =
            preserveLegacyTagMetadataAuthority;
    }

    public static ContainerRegistryAuthority Current =>
        _current ??= Create(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public string DnsSuffix { get; }

    public bool PreserveLegacyTagMetadataAuthority { get; }

    public static ContainerRegistryAuthority Initialize()
    {
        var authority = Create(Environment.GetEnvironmentVariable(EnvironmentVariable));
        _current = authority;
        return authority;
    }

    public static ContainerRegistryAuthority Create(
        string? configuredLoginServerAuthoritySuffix)
    {
        var preserveLegacyTagMetadataAuthority =
            configuredLoginServerAuthoritySuffix is null;
        var value = configuredLoginServerAuthoritySuffix is null
            ? GlobalSettings.DefaultContainerRegistryLoginServerAuthoritySuffix
            : configuredLoginServerAuthoritySuffix;
        if (!TryParse(value, out var dnsSuffix, out var port))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must be a login-server authority suffix in host[:port] form.");
        }

        return new ContainerRegistryAuthority(
            dnsSuffix,
            port,
            preserveLegacyTagMetadataAuthority);
    }

    public string GetLoginServer(string registryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryName);
        return $"{registryName.ToLowerInvariant()}.{_loginServerSuffix}";
    }

    public string GetTagMetadataRegistry(string registryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryName);
        return PreserveLegacyTagMetadataAuthority
            ? $"{registryName}.azurecr.io"
            : GetLoginServer(registryName);
    }

    private static bool TryParse(string value, out string dnsSuffix, out int? port)
    {
        dnsSuffix = string.Empty;
        port = null;

        if (value.Length == 0
            || value.Length > 259
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.ContainsAny(['/', '\\', '@', '?', '#']))
        {
            return false;
        }

        var host = value;
        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            if (value.AsSpan(0, colon).Contains(':')
                || value.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') >= 0
                || !int.TryParse(
                    value.AsSpan(colon + 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedPort)
                || parsedPort is < 1 or > 65535)
            {
                return false;
            }

            host = value[..colon];
            port = parsedPort;
        }

        if (host.Length is < 1 or > 253
            || host.StartsWith('.')
            || host.EndsWith('.'))
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63
                || !char.IsAsciiLetterOrDigit(label[0])
                || !char.IsAsciiLetterOrDigit(label[^1])
                || label.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            {
                return false;
            }
        }

        dnsSuffix = host.ToLowerInvariant();
        return true;
    }
}
