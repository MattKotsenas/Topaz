using System.Text.Json;

namespace Topaz.Service.ContainerRegistry;

internal sealed class ContainerRegistryNameBoundary
{
    private const string EnvironmentVariable =
        "TOPAZ_CONTAINER_REGISTRY_NAME";

    private static ContainerRegistryNameBoundary? _current;
    private readonly string? _configuredName;

    private ContainerRegistryNameBoundary(string? configuredName)
    {
        _configuredName = configuredName;
    }

    public static ContainerRegistryNameBoundary Current =>
        _current ??= Create(
            Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static ContainerRegistryNameBoundary Initialize()
    {
        var boundary = Create(
            Environment.GetEnvironmentVariable(EnvironmentVariable));
        _current = boundary;
        return boundary;
    }

    public static ContainerRegistryNameBoundary Create(
        string? configuredName)
    {
        if (configuredName is null)
        {
            return new ContainerRegistryNameBoundary(null);
        }

        if (!IsValidName(configuredName))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must contain 5-50 ASCII letters or digits.");
        }

        return new ContainerRegistryNameBoundary(configuredName);
    }

    public bool Allows(string registryName) =>
        _configuredName is null
        || string.Equals(
            _configuredName,
            registryName,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsValidName(string registryName) =>
        registryName.Length is >= 5 and <= 50
        && registryName.All(char.IsAsciiLetterOrDigit);

    public string MismatchReason(string registryName) =>
        $"The registry name '{registryName}' does not match the configured registry "
        + $"'{_configuredName}'.";

    public bool TryFindMismatchedRegistry(
        JsonElement scope,
        out string registryName)
    {
        if (scope.ValueKind != JsonValueKind.Object
            || !TryGetProperty(
                scope,
                "resources",
                out var resources)
            || resources.ValueKind != JsonValueKind.Array)
        {
            registryName = string.Empty;
            return false;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = TryGetProperty(resource, "type", out var typeNode)
                && typeNode.ValueKind == JsonValueKind.String
                    ? typeNode.GetString()
                    : null;
            var name = TryGetProperty(resource, "name", out var nameNode)
                && nameNode.ValueKind == JsonValueKind.String
                    ? nameNode.GetString()
                    : null;
            if (string.Equals(
                    type,
                    "Microsoft.ContainerRegistry/registries",
                    StringComparison.OrdinalIgnoreCase)
                && name is not null
                && !name.StartsWith('[')
                && !Allows(name))
            {
                registryName = name;
                return true;
            }

            if (TryFindMismatchedRegistry(resource, out registryName))
            {
                return true;
            }

            if (TryGetProperty(resource, "properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && TryGetProperty(
                    properties,
                    "template",
                    out var nestedTemplate)
                && TryFindMismatchedRegistry(
                    nestedTemplate,
                    out registryName))
            {
                return true;
            }
        }

        registryName = string.Empty;
        return false;
    }

    private static bool TryGetProperty(
        JsonElement value,
        string name,
        out JsonElement propertyValue)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                propertyValue = property.Value;
                return true;
            }
        }

        propertyValue = default;
        return false;
    }
}
