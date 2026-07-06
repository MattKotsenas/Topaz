using System.Collections.Concurrent;
using System.Threading;

using Topaz.ResourceManager;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Service.Storage.Models.Requests;
using Topaz.Shared;

using Topaz.Dns;

namespace Topaz.Tests;

/// <summary>
/// Regression guard for the control-plane concurrent-create race: an ARM copy loop provisions several
/// storage accounts under the same resource group concurrently, so many threads create resources at once.
/// Each create records a global DNS entry, and <see cref="Topaz.Dns.GlobalDnsEntries"/> did an unsynchronised
/// read-modify-write of one shared <c>global-dns.json</c> - concurrent callers observed a half-written/empty
/// file (<c>JsonException</c>) and lost each other's updates, which surfaced as intermittent deployment
/// failures. This test drives many concurrent creates through the real control-plane path and asserts every
/// account is fully persisted. Reliably reproduces on Linux (the container platform) without the fix.
/// </summary>
public sealed class ControlPlaneConcurrentCreateTests
{
    [Test]
    public async Task Concurrent_create_of_many_instances_under_one_resource_group_persists_every_metadata_file()
    {
        var logger = new SilentLogger();
        // The static DNS logger writes to a shared topaz.log; silence it so its (separate) non-thread-safe
        // file append does not mask the directory-creation race under test.
        GlobalDnsEntries.ConfigureLogger(logger);
        // The real deployment path: AzureStorageControlPlane.CreateOrUpdate creates the account dir + metadata
        // (with a DNS entry, createOperation=true) and then writes properties.xml into the same instance dir.
        var controlPlane = new AzureStorageControlPlane(new StorageResourceProvider(logger), logger);
        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("concurrentcreaterg");

        const int instanceCount = 48;
        var names = Enumerable.Range(0, instanceCount).Select(i => $"cca{i:D4}acct").ToArray();

        var errors = new ConcurrentBag<Exception>();
        using var gate = new ManualResetEventSlim(false);

        var workers = names.Select(name => Task.Run(() =>
        {
            // Release all threads at once so their directory creation races on the shared parent.
            gate.Wait();
            try
            {
                controlPlane.CreateOrUpdate(subscription, resourceGroup, name, new CreateOrUpdateStorageAccountRequest
                {
                    Location = "eastus",
                    Sku = new ResourceSku { Name = "Standard_LRS" },
                    Kind = "StorageV2",
                });
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        gate.Set();
        await Task.WhenAll(workers);

        try
        {
            Assert.That(errors, Is.Empty, () => errors.First().ToString());
            foreach (var name in names)
            {
                var instancePath = controlPlane.GetServiceInstancePath(subscription, resourceGroup, name);
                Assert.That(File.Exists(Path.Combine(instancePath, "metadata.json")), Is.True, $"metadata.json missing for '{name}'");
                Assert.That(File.Exists(Path.Combine(instancePath, "properties.xml")), Is.True, $"properties.xml missing for '{name}'");
            }
        }
        finally
        {
            var subscriptionRoot = Path.Combine(GlobalSettings.MainEmulatorDirectory, ".subscription", subscription.Value.ToString());
            if (Directory.Exists(subscriptionRoot))
            {
                Directory.Delete(subscriptionRoot, recursive: true);
            }
        }
    }

#pragma warning disable CS0618 // implementing the obsolete LogDebug(string) overload for the no-op logger
    private sealed class SilentLogger : ITopazLogger
    {
        public LogLevel LogLevel => LogLevel.Error;
        public void LogInformation(string message) { }
        public void LogDebug(string message) { }
        public void LogDebug(string methodName, string message) { }
        public void LogDebug(string className, string methodName, params object[] parameters) { }
        public void LogDebug(string className, string methodName, string template, params object?[] parameters) { }
        public void LogError(Exception ex) { }
        public void LogError(string message) { }
        public void LogError(string className, string methodName, string template, params object?[] parameters) { }
        public void LogWarning(string message) { }
        public void SetLoggingLevel(LogLevel level) { }
        public void EnableLoggingToFile(bool refreshLog) { }
        public void ConfigureIdFactory(CorrelationIdFactory idFactory) { }
    }
#pragma warning restore CS0618
}
