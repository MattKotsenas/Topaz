using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Direct, host-free coverage of the table data-plane under concurrent writers and readers of the
/// SAME entity. The data plane is file-backed; a non-atomic write (truncate-then-write, or a
/// delete-then-write gap) lets a concurrent reader observe an empty/partial file (JSON parse failure)
/// or a missing file (EntityNotFound), which the emulator surfaces as a 500. A high-frequency writer
/// (e.g. a fast job-poll updating one row) overlapping a reader reproduced this; callers that do not
/// retry 500 then failed intermittently. The data plane serialises per-entity access so a reader always
/// sees complete old-or-new content - never torn or missing.
/// </summary>
[TestFixture]
public class TableConcurrencyTests
{
    private static IHeaderDictionary Unconditional()
        => new HeaderDictionary { ["If-Match"] = "*" };

    private static System.IO.Stream Body(string json)
        => new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));

    [Test]
    public void ConcurrentUpdatesAndReads_NeverObserveTornOrMissingEntity()
    {
        var logger = new PrettyTopazLogger();
        var provider = new TableResourceProvider(logger);
        var dataPlane = new TableServiceDataPlane(provider, logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string table = "ConcurrencyTable";
        const string pk = "pk1";
        const string rk = "rk1";

        var dataDir = provider.GetTableDataPath(subscription, resourceGroup, table, account);
        Directory.CreateDirectory(dataDir);

        const int writerTasks = 4;
        const int updatesPerWriter = 150;
        const int readerTasks = 4;

        try
        {
            // Seed the single contended entity.
            dataPlane.InsertEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"0"}"""),
                subscription, resourceGroup, table, account);

            var readerErrors = new ConcurrentBag<Exception>();
            var writerErrors = new ConcurrentBag<Exception>();
            var reads = 0;
            var writesDone = 0;
            using var writersComplete = new CountdownEvent(writerTasks);

            var writers = Enumerable.Range(0, writerTasks).Select(w => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < updatesPerWriter; i++)
                    {
                        // Unconditional update on the SAME entity from every writer = maximum write
                        // contention on one file, which is exactly the fast-writer-vs-reader overlap.
                        // Pre-fix, one writer's File.Delete made another's File.ReadAllText throw here.
                        dataPlane.UpdateEntity(
                            Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"{{i}}","Writer":"{{w}}"}"""),
                            subscription, resourceGroup, table, account, pk, rk, Unconditional());
                        Interlocked.Increment(ref writesDone);
                    }
                }
                catch (Exception ex)
                {
                    writerErrors.Add(ex);
                }
                finally
                {
                    writersComplete.Signal();
                }
            })).ToArray();

            var readers = Enumerable.Range(0, readerTasks).Select(_ => Task.Run(() =>
            {
                while (!writersComplete.IsSet)
                {
                    try
                    {
                        // A torn write would make this Parse throw; a delete-then-write gap would make
                        // GetEntity throw EntityNotFound. Either is the corruption we are guarding against.
                        var json = dataPlane.GetEntity(subscription, resourceGroup, table, account, pk, rk);
                        var node = JsonNode.Parse(json)!.AsObject();
                        // The keys must always be present and intact in any consistent snapshot.
                        if ((string?)node["PartitionKey"] != pk || (string?)node["RowKey"] != rk)
                        {
                            readerErrors.Add(new InvalidOperationException(
                                "Reader observed an entity with wrong/missing keys: " + json));
                        }

                        Interlocked.Increment(ref reads);
                    }
                    catch (Exception ex)
                    {
                        readerErrors.Add(ex);
                    }
                }
            })).ToArray();

            Task.WaitAll(writers.Concat(readers).ToArray(), TimeSpan.FromSeconds(60));

            // The test must actually have exercised the race, otherwise a clean result is vacuous.
            Assert.That(writerErrors, Is.Empty,
                "no writer may fail under concurrent same-entity updates; first error: "
                + (writerErrors.FirstOrDefault()?.ToString() ?? "<none>"));
            Assert.That(writesDone, Is.EqualTo(writerTasks * updatesPerWriter),
                "precondition: all writes completed");
            Assert.That(reads, Is.GreaterThan(0),
                "precondition: readers actually read the contended entity during the writes");

            Assert.That(readerErrors, Is.Empty,
                "no reader may observe a torn/empty/missing entity under concurrent writes; first error: "
                + (readerErrors.FirstOrDefault()?.ToString() ?? "<none>"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); }
            catch (IOException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
        }
    }
}
