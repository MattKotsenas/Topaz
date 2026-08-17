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
        JsonElement element,
        out string registryName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? type = null;
            string? name = null;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    if (string.Equals(
                            property.Name,
                            "type",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        type = property.Value.GetString();
                    }
                    else if (string.Equals(
                                 property.Name,
                                 "name",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        name = property.Value.GetString();
                    }
                }

                if (TryFindMismatchedRegistry(
                        property.Value,
                        out registryName))
                {
                    return true;
                }
            }

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
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindMismatchedRegistry(item, out registryName))
                {
                    return true;
                }
            }
        }

        registryName = string.Empty;
        return false;
    }
}
