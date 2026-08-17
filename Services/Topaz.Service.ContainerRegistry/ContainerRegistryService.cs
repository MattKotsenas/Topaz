using System.Text.Json.Nodes;

using Topaz.EventPipeline;
using Topaz.Service.ContainerRegistry.Endpoints.Auth;
using Topaz.Service.ContainerRegistry.Endpoints.Blobs;
using Topaz.Service.ContainerRegistry.Endpoints.Credentials;
using Topaz.Service.ContainerRegistry.Endpoints.Manifests;
using Topaz.Service.ContainerRegistry.Endpoints.Repositories;
using Topaz.Service.ContainerRegistry.Endpoints.Registry;
using Topaz.Service.ContainerRegistry.Endpoints.Tags;
using Topaz.Service.ContainerRegistry.Endpoints.Runs;
using Topaz.Service.ContainerRegistry.Endpoints.Tasks;
using Topaz.Service.ResourceGroup;
using Topaz.Service.Shared;
using Topaz.Shared;

namespace Topaz.Service.ContainerRegistry;

public sealed class ContainerRegistryService(Pipeline eventPipeline, ITopazLogger logger) : IServiceDefinition
{
    public static bool IsGlobalService => true;
    public static string LocalDirectoryPath => Path.Combine(ResourceGroupService.LocalDirectoryPath, ".container-registry");
    public static IReadOnlyCollection<string>? Subresources => ["tasks", "runs"];
    public static string UniqueName => "container-registry";

    public string Name => "Azure Container Registry";

    public IReadOnlyCollection<IEndpointDefinition> Endpoints =>
    [
        new RunAcrTaskEndpoint(eventPipeline, logger),
        new GetAcrRunEndpoint(eventPipeline, logger),
        new ListAcrRunsEndpoint(eventPipeline, logger),
        new UpdateAcrRunEndpoint(eventPipeline, logger),
        new GetAcrRunLogSasUrlEndpoint(eventPipeline, logger),
        new ScheduleAcrRunEndpoint(eventPipeline, logger),
        new GetAcrRunOperationStatusEndpoint(eventPipeline, logger),
        new GetAcrRunLogContentEndpoint(eventPipeline, logger),
        new HeadAcrRunLogEndpoint(),
        new CreateOrUpdateAcrTaskEndpoint(eventPipeline, logger),
        new GetAcrTaskEndpoint(eventPipeline, logger),
        new GetAcrTaskDetailsEndpoint(eventPipeline, logger),
        new DeleteAcrTaskEndpoint(eventPipeline, logger),
        new ListAcrTasksEndpoint(eventPipeline, logger),
        new UpdateAcrTaskEndpoint(eventPipeline, logger),
        new CreateOrUpdateContainerRegistryEndpoint(eventPipeline, logger),
        new GetContainerRegistryEndpoint(eventPipeline, logger),
        new ListContainerRegistriesByResourceGroupEndpoint(eventPipeline, logger),
        new ListContainerRegistriesBySubscriptionEndpoint(eventPipeline, logger),
        new DeleteContainerRegistryEndpoint(eventPipeline, logger),
        new UpdateContainerRegistryEndpoint(eventPipeline, logger),
        new CheckContainerRegistryNameAvailabilityEndpoint(eventPipeline, logger),
        new AcrV2ChallengeEndpoint(ContainerRegistryControlPlane.New(eventPipeline, logger), logger),
        new AcrOAuth2ExchangeEndpoint(logger),
        new AcrOAuth2GetTokenEndpoint(ContainerRegistryControlPlane.New(eventPipeline, logger), logger),
        new AcrOAuth2PostTokenEndpoint(ContainerRegistryControlPlane.New(eventPipeline, logger), logger),
        new ListContainerRegistryCredentialsEndpoint(eventPipeline, logger),
        new GenerateContainerRegistryCredentialsEndpoint(eventPipeline, logger),
        new RegenerateContainerRegistryCredentialEndpoint(eventPipeline, logger),
        new ListContainerRegistryUsagesEndpoint(eventPipeline, logger),
        new ListReplicationsEndpoint(),
        // Data plane — blob uploads (OCI Distribution Spec)
        new InitiateBlobUploadEndpoint(AcrDataPlane(), logger),
        new PatchBlobUploadEndpoint(AcrDataPlane(), logger),
        new CompleteBlobUploadEndpoint(AcrDataPlane(), logger),
        new HeadBlobEndpoint(AcrDataPlane(), logger),
        new GetBlobEndpoint(AcrDataPlane(), logger),
        new DeleteBlobEndpoint(AcrDataPlane(), logger),
        new PutManifestEndpoint(AcrDataPlane(), logger),
        new GetManifestEndpoint(AcrDataPlane(), logger),
        new HeadManifestEndpoint(AcrDataPlane(), logger),
        new DeleteManifestEndpoint(AcrDataPlane(), logger),
        new ListRepositoriesEndpoint(AcrDataPlane(), logger),
        new ListTagsEndpoint(AcrDataPlane(), logger),
        new GetTagEndpoint(AcrDataPlane(), logger),
        new DeleteTagEndpoint(AcrDataPlane(), logger),
        new DeleteRepositoryEndpoint(AcrDataPlane(), logger),
    ];

    internal static void InitializeAuthority()
    {
        _ = ContainerRegistryAuthority.Initialize();
    }

    private AcrDataPlane AcrDataPlane() =>
        new(new ContainerRegistryResourceProvider(logger), logger);

    internal static void ApplyPersistedLoginServerAuthority(
        string emulatorDirectory,
        ContainerRegistryAuthority authority,
        Action<int>? beforeCommit = null,
        Action<int>? beforeRollback = null)
    {
        using var update = StageRegistryMetadataUpdate(
            emulatorDirectory,
            authority);
        update.Apply(beforeCommit, beforeRollback);
        update.Commit();
    }

    internal static StagedRegistryMetadataUpdate StageRegistryMetadataUpdate(
        string emulatorDirectory)
    {
        return StageRegistryMetadataUpdate(
            emulatorDirectory,
            ContainerRegistryAuthority.Current);
    }

    internal static StagedRegistryMetadataUpdate StageRegistryMetadataUpdate(
        string emulatorDirectory,
        ContainerRegistryAuthority authority)
    {
        if (!Directory.Exists(emulatorDirectory))
        {
            return new StagedRegistryMetadataUpdate([]);
        }

        var projections = new List<PersistedProjection>();
        foreach (var metadataPath in EnumerateRegistryMetadataPaths(emulatorDirectory))
        {
            var resource = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject()
                ?? throw new InvalidDataException(
                    $"Container Registry metadata must be a JSON object: {metadataPath}");
            var registryName = resource["name"]?.GetValue<string>()
                ?? throw new InvalidDataException(
                    $"Container Registry metadata has no resource name: {metadataPath}");
            var properties = resource["properties"]?.AsObject()
                ?? throw new InvalidDataException(
                    $"Container Registry metadata has no properties object: {metadataPath}");
            var loginServer = authority.GetLoginServer(registryName);
            if (string.Equals(
                    properties["loginServer"]?.GetValue<string>(),
                    loginServer,
                    StringComparison.Ordinal))
            {
                continue;
            }

            properties["loginServer"] = loginServer;
            projections.Add(new PersistedProjection(
                metadataPath,
                $"{metadataPath}.{Guid.NewGuid():N}.tmp",
                $"{metadataPath}.{Guid.NewGuid():N}.backup",
                resource.ToJsonString(GlobalSettings.JsonOptions)));
        }

        try
        {
            foreach (var projection in projections)
            {
                File.WriteAllText(projection.TemporaryPath, projection.Content);
            }
        }
        catch
        {
            foreach (var projection in projections)
            {
                if (File.Exists(projection.TemporaryPath))
                {
                    File.Delete(projection.TemporaryPath);
                }
            }

            throw;
        }

        return new StagedRegistryMetadataUpdate(projections);
    }

    private static IEnumerable<string> EnumerateRegistryMetadataPaths(
        string emulatorDirectory)
    {
        EnsurePhysicalDirectory(emulatorDirectory);
        var subscriptionsDirectory = Path.Combine(
            emulatorDirectory,
            ".subscription");
        if (!Directory.Exists(subscriptionsDirectory))
        {
            yield break;
        }
        EnsurePhysicalDirectory(subscriptionsDirectory);

        foreach (var subscriptionDirectory in Directory.EnumerateDirectories(
                     subscriptionsDirectory))
        {
            EnsurePhysicalDirectory(subscriptionDirectory);
            var resourceGroupsDirectory = Path.Combine(
                subscriptionDirectory,
                ".resource-group");
            if (!Directory.Exists(resourceGroupsDirectory))
            {
                continue;
            }
            EnsurePhysicalDirectory(resourceGroupsDirectory);

            foreach (var resourceGroupDirectory in Directory.EnumerateDirectories(
                         resourceGroupsDirectory))
            {
                EnsurePhysicalDirectory(resourceGroupDirectory);
                var registriesDirectory = Path.Combine(
                    resourceGroupDirectory,
                    ".container-registry");
                if (!Directory.Exists(registriesDirectory))
                {
                    continue;
                }
                EnsurePhysicalDirectory(registriesDirectory);

                foreach (var registryDirectory in Directory.EnumerateDirectories(
                             registriesDirectory))
                {
                    EnsurePhysicalDirectory(registryDirectory);
                    var metadataPath = Path.Combine(
                        registryDirectory,
                        "metadata.json");
                    if (File.Exists(metadataPath))
                    {
                        EnsurePhysicalFile(metadataPath);
                        yield return metadataPath;
                    }
                }
            }
        }
    }

    private static void EnsurePhysicalDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Container Registry metadata path cannot contain a reparse point: {path}");
        }
    }

    private static void EnsurePhysicalFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Container Registry metadata cannot be a reparse point: {path}");
        }
    }

    internal sealed class StagedRegistryMetadataUpdate(
        IReadOnlyList<PersistedProjection> projections) : IDisposable
    {
        private readonly List<PersistedProjection> committed = [];
        private bool completed;
        private bool _retainBackups;

        public void Apply(
            Action<int>? beforeCommit = null,
            Action<int>? beforeRollback = null)
        {
            if (completed || committed.Count > 0)
            {
                throw new InvalidOperationException(
                    "Container Registry authority projection has already been committed.");
            }

            try
            {
                for (var index = 0; index < projections.Count; index++)
                {
                    beforeCommit?.Invoke(index);
                    var projection = projections[index];
                    File.Copy(
                        projection.MetadataPath,
                        projection.BackupPath,
                        overwrite: true);
                    File.Move(
                        projection.TemporaryPath,
                        projection.MetadataPath,
                        overwrite: true);
                    committed.Add(projection);
                }
            }
            catch (Exception commitException)
            {
                var rollbackErrors = RollbackCore(beforeRollback);
                if (rollbackErrors.Count > 0)
                {
                    _retainBackups = true;
                    throw new AggregateException(
                        "Container Registry authority migration failed and could not be fully rolled back.",
                        new[] { commitException }.Concat(rollbackErrors));
                }

                throw;
            }
        }

        public void Rollback()
        {
            var rollbackErrors = RollbackCore(beforeRollback: null);
            if (rollbackErrors.Count > 0)
            {
                _retainBackups = true;
                throw new AggregateException(
                    "Container Registry authority migration could not be fully rolled back.",
                    rollbackErrors);
            }
        }

        public void Commit()
        {
            if (committed.Count != projections.Count)
            {
                throw new InvalidOperationException(
                    "Container Registry authority projection has not been committed.");
            }

            foreach (var projection in projections)
            {
                File.Delete(projection.BackupPath);
            }
            completed = true;
        }

        public void Dispose()
        {
            foreach (var projection in projections)
            {
                if (File.Exists(projection.TemporaryPath))
                {
                    File.Delete(projection.TemporaryPath);
                }

                if (!_retainBackups
                    && (completed || committed.Count == 0)
                    && File.Exists(projection.BackupPath))
                {
                    File.Delete(projection.BackupPath);
                }
            }
        }

        private List<Exception> RollbackCore(Action<int>? beforeRollback)
        {
            var rollbackErrors = new List<Exception>();
            for (var index = committed.Count - 1; index >= 0; index--)
            {
                var projection = committed[index];
                try
                {
                    beforeRollback?.Invoke(index);
                    File.Move(
                        projection.BackupPath,
                        projection.MetadataPath,
                        overwrite: true);
                    committed.RemoveAt(index);
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException);
                }
            }

            return rollbackErrors;
        }
    }

    internal sealed record PersistedProjection(
        string MetadataPath,
        string TemporaryPath,
        string BackupPath,
        string Content);
}
