using System.Text.Json;
using Topaz.Identity;
using Topaz.Shared;

namespace Topaz.CloudEnvironment.Models.Responses;

internal sealed class GetMetadataEndpointResponse(EntraAuthority? configuredAuthority = null)
{
    private EntraAuthority Authority { get; } = configuredAuthority ?? EntraAuthority.Current;

    public string ResourceManager => GlobalSettings.DefaultResourceManagerOrigin;

    public string ResourceManagerEndpoint => GlobalSettings.DefaultResourceManagerOrigin;

    public string ActiveDirectory => Authority.Origin;

    public string ActiveDirectoryEndpoint => Authority.Origin;

    public string ActiveDirectoryResourceId => Authority.Origin;

    // Graph resource audiences remain on the local control-plane origin; only Entra login endpoints
    // follow the configurable identity authority.
    public string ActiveDirectoryGraphResourceId => GlobalSettings.DefaultResourceManagerOrigin;

    public string MicrosoftGraphResourceId => $"{GlobalSettings.DefaultResourceManagerOrigin}/";

    public IReadOnlyDictionary<string, string> Endpoints => new Dictionary<string, string>
    {
        { "resourceManager", GlobalSettings.DefaultResourceManagerOrigin },
        { "resourceManagerEndpoint", GlobalSettings.DefaultResourceManagerOrigin },
        { "activeDirectory", Authority.Origin },
        { "activeDirectoryEndpoint", Authority.Origin },
        { "activeDirectoryResourceId", Authority.Origin },
        { "activeDirectoryGraphResourceId", GlobalSettings.DefaultResourceManagerOrigin },
        { "microsoftGraphResourceId", $"{GlobalSettings.DefaultResourceManagerOrigin}/" }
    };
    
    public IReadOnlyDictionary<string, string> Suffixes => new Dictionary<string, string>
    {
        { "azureDataLakeStoreFileSystem", "datalake.topaz.local.dev" },
        { "acrLoginServer", GlobalSettings.ContainerRegistryLoginDnsSuffix },
        { "sqlServerHostname", "sql.topaz.local.dev" },
        { "azureDataLakeAnalyticsCatalogAndJob", "analytics.topaz.local.dev" },
        { "keyVaultDns", "vault.topaz.local.dev" },
        { "storage", $"storage.topaz.local.dev:{GlobalSettings.DefaultStoragePort}" },
        { "azureFrontDoorEndpointSuffix", "frontdoor.topaz.local.dev" },
        { "storageSyncEndpointSuffix", "storagesync.topaz.local.dev" },
        { "mhsmDns", "managedhsm.topaz.local.dev" },
        { "mysqlServerEndpoint", "mysql.topaz.local.dev" },
        { "postgresqlServerEndpoint", "postgres.topaz.local.dev" },
        { "mariadbServerEndpoint", "mariadb.topaz.local.dev" },
        { "synapseAnalytics", "synapse.topaz.local.dev" },
        { "attestationEndpoint", "attest.topaz.local.dev" }
    };

    public string Name => "public";

    public AuthenticationMetadata Authentication => new(Authority);

    internal class AuthenticationMetadata(EntraAuthority authority)
    {
        public string LoginEndpoint => authority.GetEndpoint("/");

        public string IdentityProvider => "AAD";

        public string Tenant => "common";
        
        public string[] Audiences => [
            "https://management.core.windows.net/",
            "https://management.azure.com/"
        ];
    }

    public override string ToString()
    {
        return JsonSerializer.Serialize(this, GlobalSettings.JsonOptions);
    }
}