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

    /// <summary>
    /// Coverage of the on-disk durability invariant the per-entity lock alone does NOT provide: the entity FILE
    /// must never be observed (or left) zero-byte or partial. <see cref="ConcurrentUpdatesAndReads_NeverObserveTornOrMissingEntity"/>
    /// reads through <c>GetEntity</c>, which holds the per-entity lock, so it never sees a write in progress. But a
    /// non-atomic File.WriteAllText (truncate-then-write) leaves the file transiently - and, if the write is
    /// interrupted after the truncate (an ASP.NET Core request abort / thread teardown mid-write), PERSISTENTLY -
    /// zero bytes. A 0-byte stored entity is exactly what stranded a background job and wedged a consumer
    /// deployment. This test observes the raw file directly (bypassing the lock) under concurrent writes and asserts it
    /// is never zero-byte or unparseable - which holds only because writes are now atomic (temp file + rename).
    /// </summary>
    [Test]
    public void ConcurrentWrites_TheEntityFileOnDiskIsNeverTornOrZeroByte()
    {
        var logger = new PrettyTopazLogger();
        var provider = new TableResourceProvider(logger);
        var dataPlane = new TableServiceDataPlane(provider, logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string table = "AtomicTable";
        const string pk = "pk1";
        const string rk = "rk1";

        var dataDir = provider.GetTableDataPath(subscription, resourceGroup, table, account);
        Directory.CreateDirectory(dataDir);
        var entityFile = Path.Combine(dataDir, $"{pk}_{rk}.json");

        const int writerTasks = 4;
        const int updatesPerWriter = 200;

        try
        {
            dataPlane.InsertEntity(
                Body($$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"0"}"""),
                subscription, resourceGroup, table, account);

            var observerErrors = new ConcurrentBag<Exception>();
            var observations = 0;
            var writesDone = 0;
            var writerErrors = new ConcurrentBag<Exception>();
            using var writersComplete = new CountdownEvent(writerTasks);

            var writers = Enumerable.Range(0, writerTasks).Select(w => Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < updatesPerWriter; i++)
                    {
                        var json = $$"""{"PartitionKey":"{{pk}}","RowKey":"{{rk}}","Counter":"{{i}}","Writer":"{{w}}"}""";
                        for (var attempt = 1; ; attempt++)
                        {
                            try
                            {
                                dataPlane.UpdateEntity(Body(json), subscription, resourceGroup, table, account, pk, rk, Unconditional());
                                break;
                            }
                            catch (UnauthorizedAccessException) when (attempt < 100)
                            {
                                // Windows-only artifact: MoveFileEx(REPLACE_EXISTING) cannot replace a file the
                                // unlocked raw observer below holds open. The Linux production target's rename(2)
                                // succeeds over open files, so AtomicFile never throws there; this retry just keeps
                                // the Windows dev-box test honest without weakening the on-disk assertion.
                                Thread.Sleep(2);
                            }
                        }
                        Interlocked.Increment(ref writesDone);
                    }
                }
                catch (Exception ex) { writerErrors.Add(ex); }
                finally { writersComplete.Signal(); }
            })).ToArray();

            // A single raw observer reads the file bytes directly - NOT via GetEntity, so it is not serialised with
            // the writers by the per-entity lock - modelling an unlocked reader (e.g. a table query scan). It opens
            // with FileShare.Delete and yields briefly between reads so it does not hold the file open continuously:
            // on Windows MoveFileEx cannot replace a file whose name is held open, so a continuous unlocked reader
            // would starve the writer's atomic rename (in production GetEntity reads under the same lock as the
            // write, so they never overlap). A non-atomic truncate-then-write still exposes its 0-byte / partial
            // window to this reader; the atomic temp+rename never does.
            var observers = Enumerable.Range(0, 1).Select(_ => Task.Run(() =>
            {
                while (!writersComplete.IsSet)
                {
                    try
                    {
                        byte[] bytes;
                        using (var fs = new FileStream(entityFile, FileMode.Open, FileAccess.Read,
                                   FileShare.ReadWrite | FileShare.Delete))
                        {
                            bytes = new byte[fs.Length];
                            var off = 0;
                            while (off < bytes.Length)
                            {
                                var n = fs.Read(bytes, off, bytes.Length - off);
                                if (n == 0) break;
                                off += n;
                            }
                        }
                        if (bytes.Length == 0)
                        {
                            observerErrors.Add(new IOException("entity file observed at 0 bytes (non-atomic truncate-then-write)"));
                            continue;
                        }
                        JsonNode.Parse(Encoding.UTF8.GetString(bytes)); // partial content => JsonException
                        Interlocked.Increment(ref observations);
                    }
                    catch (System.Text.Json.JsonException ex) { observerErrors.Add(ex); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        /* file briefly unopenable during the atomic rename - expected on Windows, retry */
                    }

                    Thread.Sleep(1);
                }
            })).ToArray();

            Task.WaitAll(writers.Concat(observers).ToArray(), TimeSpan.FromSeconds(60));

            Assert.That(writerErrors, Is.Empty,
                "no writer may fail; first error: " + (writerErrors.FirstOrDefault()?.ToString() ?? "<none>"));
            Assert.That(writesDone, Is.EqualTo(writerTasks * updatesPerWriter), "precondition: all writes completed");
            Assert.That(observations, Is.GreaterThan(0), "precondition: observers actually read the file during writes");

            Assert.That(observerErrors, Is.Empty,
                "the entity file must never be observed zero-byte or partial on disk - writes must be atomic; first error: "
                + (observerErrors.FirstOrDefault()?.ToString() ?? "<none>"));

            // And it must be left complete + parseable after the storm (a persistent 0-byte file is the stall).
            var finalBytes = File.ReadAllBytes(entityFile);
            Assert.That(finalBytes.Length, Is.GreaterThan(0), "the entity file must not be left zero-byte");
            Assert.That(() => JsonNode.Parse(Encoding.UTF8.GetString(finalBytes)), Throws.Nothing,
                "the final entity file must be valid JSON (a torn write would strand any reader of this entity)");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); }
            catch (IOException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
        }
    }
}
