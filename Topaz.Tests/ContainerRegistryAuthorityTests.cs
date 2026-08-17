using Topaz.Shared;
using Topaz.Service.ContainerRegistry;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topaz.Tests;

public class ContainerRegistryAuthorityTests
{
    [Test]
    public void Configured_suffix_drives_registry_login_server()
    {
        var authority = ContainerRegistryAuthority.Create("azurecr.test");

        Assert.That(
            authority.GetLoginServer("sampleacr01"),
            Is.EqualTo("sampleacr01.azurecr.test"));
    }

    [Test]
    public void Registry_name_is_canonicalized_for_the_login_server()
    {
        var authority = ContainerRegistryAuthority.Create("azurecr.test");

        Assert.That(
            authority.GetLoginServer("SampleAcr01"),
            Is.EqualTo("sampleacr01.azurecr.test"));
    }

    [Test]
    public void Default_suffix_returns_local_login_server()
    {
        var authority = ContainerRegistryAuthority.Create(null);

        Assert.That(
            authority.GetLoginServer("topazacr01"),
            Is.EqualTo("topazacr01.cr.topaz.local.dev:8892"));
    }

    [Test]
    public void Default_tag_metadata_retains_the_Azure_registry_name()
    {
        Assert.That(
            ContainerRegistryAuthority.Create(null)
                .GetTagMetadataRegistry("topazacr01"),
            Is.EqualTo("topazacr01.azurecr.io"));
    }

    [Test]
    public void Configured_tag_metadata_uses_the_composed_login_server()
    {
        Assert.That(
            ContainerRegistryAuthority.Create("azurecr.test")
                .GetTagMetadataRegistry("sampleacr01"),
            Is.EqualTo("sampleacr01.azurecr.test"));
    }

    [TestCase("https://azurecr.test")]
    [TestCase("azurecr.test/path")]
    [TestCase("azurecr.test/")]
    [TestCase("azurecr.test?query=true")]
    [TestCase("user@azurecr.test")]
    [TestCase(".azurecr.test")]
    [TestCase("azurecr.test.")]
    [TestCase("azurecr.test:")]
    [TestCase("azurecr.test ")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("azurecr.test: 443")]
    [TestCase("azurecr.test:+443")]
    [TestCase("azurecr.test:0")]
    [TestCase("azurecr.test:65536")]
    public void Invalid_suffix_is_rejected(string suffix)
    {
        Assert.That(
            () => ContainerRegistryAuthority.Create(suffix),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo(
                    "TOPAZ_CONTAINER_REGISTRY_LOGIN_SERVER_AUTHORITY_SUFFIX must be a login-server authority suffix in host[:port] form."));
    }

    [Test]
    public void Overlong_suffix_is_rejected()
    {
        var suffix = string.Join('.', Enumerable.Repeat(new string('a', 63), 5));

        Assert.That(
            () => ContainerRegistryAuthority.Create(suffix),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    [NonParallelizable]
    public void Registry_preflight_rejects_invalid_configured_suffix()
    {
        const string Variable =
            "TOPAZ_CONTAINER_REGISTRY_LOGIN_SERVER_AUTHORITY_SUFFIX";
        var previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, "https://azurecr.test");

        try
        {
            Assert.That(
                ContainerRegistryService.InitializeAuthority,
                Throws.TypeOf<InvalidOperationException>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Test]
    [NonParallelizable]
    public void Persisted_registry_login_server_is_projected_at_startup()
    {
        var emulatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"topaz-registry-authority-{Guid.NewGuid():N}");
        var registryDirectory = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".container-registry",
            "sampleacr01");
        Directory.CreateDirectory(registryDirectory);
        var metadataPath = Path.Combine(registryDirectory, "metadata.json");
        File.WriteAllText(
            metadataPath,
            """
            {
              "name": "sampleacr01",
              "properties": {
                "loginServer": "sampleacr01.cr.topaz.local.dev:8892"
              }
            }
            """);

        try
        {
            var previous = Environment.GetEnvironmentVariable(
                "TOPAZ_CONTAINER_REGISTRY_LOGIN_SERVER_AUTHORITY_SUFFIX");
            Environment.SetEnvironmentVariable(
                "TOPAZ_CONTAINER_REGISTRY_LOGIN_SERVER_AUTHORITY_SUFFIX",
                "azurecr.test");
            try
            {
                ContainerRegistryService.InitializeAuthority();
                using var update =
                    ContainerRegistryService.StageRegistryMetadataUpdate(
                        emulatorDirectory);
                update.Apply();
                update.Commit();
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "TOPAZ_CONTAINER_REGISTRY_LOGIN_SERVER_AUTHORITY_SUFFIX",
                    previous);
                ContainerRegistryAuthority.Initialize();
            }

            var resource = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
            Assert.That(
                resource["properties"]!["loginServer"]!.GetValue<string>(),
                Is.EqualTo("sampleacr01.azurecr.test"));
        }
        finally
        {
            Directory.Delete(emulatorDirectory, recursive: true);
        }
    }

    [TestCase("azurecr.test:1", "sampleacr01.azurecr.test:1")]
    [TestCase("azurecr.test:65535", "sampleacr01.azurecr.test:65535")]
    public void Configured_port_is_preserved(
        string suffix,
        string expectedLoginServer)
    {
        Assert.That(
            ContainerRegistryAuthority.Create(suffix)
                .GetLoginServer("sampleacr01"),
            Is.EqualTo(expectedLoginServer));
    }

    [Test]
    public void Persisted_projection_validates_all_resources_before_writing()
    {
        var emulatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"topaz-registry-authority-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".container-registry",
            "firstregistry");
        var secondDirectory = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".container-registry",
            "secondregistry");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstPath = Path.Combine(firstDirectory, "metadata.json");
        File.WriteAllText(
            firstPath,
            """{"name":"firstregistry","properties":{"loginServer":"firstregistry.cr.topaz.local.dev:8892"}}""");
        File.WriteAllText(
            Path.Combine(secondDirectory, "metadata.json"),
            """{"name":"secondregistry"}""");

        try
        {
            Assert.That(
                () => ContainerRegistryService.ApplyPersistedLoginServerAuthority(
                    emulatorDirectory,
                    ContainerRegistryAuthority.Create("azurecr.test")),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(
                JsonNode.Parse(File.ReadAllText(firstPath))!["properties"]!["loginServer"]!
                    .GetValue<string>(),
                Is.EqualTo("firstregistry.cr.topaz.local.dev:8892"));
        }
        finally
        {
            Directory.Delete(emulatorDirectory, recursive: true);
        }
    }

    [Test]
    public void Persisted_projection_ignores_non_registry_metadata()
    {
        var emulatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"topaz-registry-authority-{Guid.NewGuid():N}");
        var decoyDirectory = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".azure-storage",
            ".container-registry",
            "artifact");
        Directory.CreateDirectory(decoyDirectory);
        var metadataPath = Path.Combine(decoyDirectory, "metadata.json");
        File.WriteAllText(metadataPath, "not-json");

        try
        {
            ContainerRegistryService.ApplyPersistedLoginServerAuthority(
                emulatorDirectory,
                ContainerRegistryAuthority.Create("azurecr.test"));

            Assert.That(File.ReadAllText(metadataPath), Is.EqualTo("not-json"));
        }
        finally
        {
            Directory.Delete(emulatorDirectory, recursive: true);
        }
    }

    [Test]
    public void Persisted_projection_rolls_back_committed_replacements()
    {
        var emulatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"topaz-registry-authority-{Guid.NewGuid():N}");
        var registryRoot = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".container-registry");
        var firstPath = WriteRegistryMetadata(registryRoot, "firstregistry");
        var secondPath = WriteRegistryMetadata(registryRoot, "secondregistry");

        try
        {
            Assert.That(
                () => ContainerRegistryService.ApplyPersistedLoginServerAuthority(
                    emulatorDirectory,
                    ContainerRegistryAuthority.Create("azurecr.test"),
                    beforeCommit: index =>
                    {
                        if (index == 1)
                        {
                            throw new IOException("Injected replacement failure.");
                        }
                    }),
                Throws.TypeOf<IOException>());
            Assert.Multiple(() =>
            {
                Assert.That(
                    ReadLoginServer(firstPath),
                    Is.EqualTo("firstregistry.cr.topaz.local.dev:8892"));
                Assert.That(
                    ReadLoginServer(secondPath),
                    Is.EqualTo("secondregistry.cr.topaz.local.dev:8892"));
            });
        }
        finally
        {
            Directory.Delete(emulatorDirectory, recursive: true);
        }
    }

    [Test]
    public void Staged_projection_does_not_replace_metadata_before_commit()
    {
        var emulatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"topaz-registry-authority-{Guid.NewGuid():N}");
        var registryRoot = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".container-registry");
        var metadataPath = WriteRegistryMetadata(registryRoot, "sampleacr01");

        try
        {
            using (ContainerRegistryService.StageRegistryMetadataUpdate(
                       emulatorDirectory,
                       ContainerRegistryAuthority.Create("azurecr.test")))
            {
                Assert.That(
                    ReadLoginServer(metadataPath),
                    Is.EqualTo("sampleacr01.cr.topaz.local.dev:8892"));
            }

            Assert.That(
                ReadLoginServer(metadataPath),
                Is.EqualTo("sampleacr01.cr.topaz.local.dev:8892"));
        }
        finally
        {
            Directory.Delete(emulatorDirectory, recursive: true);
        }
    }

    [Test]
    public void Failed_rollback_retains_recovery_backup()
    {
        var emulatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"topaz-registry-authority-{Guid.NewGuid():N}");
        var registryRoot = Path.Combine(
            emulatorDirectory,
            ".subscription",
            "sub",
            ".resource-group",
            "rg",
            ".container-registry");
        var firstPath = WriteRegistryMetadata(registryRoot, "firstregistry");
        WriteRegistryMetadata(registryRoot, "secondregistry");

        try
        {
            Assert.That(
                () => ContainerRegistryService.ApplyPersistedLoginServerAuthority(
                    emulatorDirectory,
                    ContainerRegistryAuthority.Create("azurecr.test"),
                    beforeCommit: index =>
                    {
                        if (index == 1)
                        {
                            throw new IOException("Injected replacement failure.");
                        }
                    },
                    beforeRollback: _ =>
                        throw new IOException("Injected rollback failure.")),
                Throws.TypeOf<AggregateException>());
            var backupPath = Directory.EnumerateFiles(
                    Path.GetDirectoryName(firstPath)!,
                    "metadata.json.*.backup")
                .Single();
            Assert.That(
                ReadLoginServer(backupPath),
                Is.EqualTo("firstregistry.cr.topaz.local.dev:8892"));
        }
        finally
        {
            Directory.Delete(emulatorDirectory, recursive: true);
        }
    }

    private static string WriteRegistryMetadata(
        string registryRoot,
        string registryName)
    {
        var registryDirectory = Path.Combine(registryRoot, registryName);
        Directory.CreateDirectory(registryDirectory);
        var metadataPath = Path.Combine(registryDirectory, "metadata.json");
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(new
            {
                name = registryName,
                properties = new
                {
                    loginServer =
                        $"{registryName}.cr.topaz.local.dev:8892"
                }
            }));
        return metadataPath;
    }

    private static string ReadLoginServer(string metadataPath) =>
        JsonNode.Parse(File.ReadAllText(metadataPath))!["properties"]!["loginServer"]!
            .GetValue<string>();
}
