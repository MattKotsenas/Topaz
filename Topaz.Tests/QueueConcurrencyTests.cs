using NUnit.Framework;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Direct, host-free coverage of the queue data-plane dequeue invariant: a single message must be handed to
/// AT MOST ONE consumer per visibility window (standard Azure Storage Queue semantics).
///
/// <para><see cref="QueueServiceDataPlane.GetMessages"/> does, per message, read-file -> IsVisible? -> bump
/// DequeueCount + set NextVisibleTime -> write-file WITHOUT serialisation. So concurrent callers all read the
/// still-visible message and all claim it - one message dequeued N times. That is the root of the
/// never-dispatches stall: the at-least-once trigger queue handed ONE setup trigger to hundreds
/// of workers at once (RegionAgnosticRolloutSetupJob entered 500+ times within ms), which then raced on the
/// per-deployment ARM token and wedged the deployment.</para>
///
/// <para>This reproduces it DETERMINISTICALLY (no end-to-end soak) via the <see cref="QueueServiceDataPlane.
/// OnDequeueClaim"/> seam, which fires when a caller passes the visibility check. The test counts how many
/// concurrent callers claim the SAME visible message, holding each at a rendezvous so they all reach the check
/// before any writeback. The claim count is platform-independent on purpose: OS file locking masks the
/// write-back race on Windows (a losing writer throws and is swallowed) but not on Linux (the container), so
/// asserting on returned counts alone would pass vacuously on the dev box. Pre-fix every caller claims the one
/// message; once the dequeue serialises per queue, the first claimer hides it under the lock and the rest must
/// observe it hidden - so exactly one claim, by construction, independent of timing or platform.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class QueueConcurrencyTests
{
    [Test]
    public void ConcurrentGetMessages_ClaimTheSameMessage_AtMostOnce()
    {
        var logger = new PrettyTopazLogger();
        var dataPlane = QueueServiceDataPlane.New(logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string queue = "concurrencyqueue";

        var create = dataPlane.CreateQueue(subscription, resourceGroup, account, queue);
        Assert.That(create.Result, Is.EqualTo(OperationResult.Created).Or.EqualTo(OperationResult.Success),
            "precondition: queue created");

        // Enqueue exactly ONE message, immediately visible (visibility timeout 0).
        var send = dataPlane.SendMessage(subscription, resourceGroup, account, queue, "hello", visibilityTimeout: 0);
        Assert.That(send.Result, Is.EqualTo(OperationResult.Success), "precondition: single message enqueued");

        const int dequeuers = 8;
        var claims = 0;
        var arrived = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var allArrived = new ManualResetEventSlim(false);

        // Rendezvous: each caller that claims the message (passes IsVisible) is held until all concurrent
        // claimants have arrived (or a short timeout), so pre-fix every caller passes the check on the SAME
        // visible message before any writeback could hide it. Post-fix only the lock holder reaches here; it
        // times out, hides the message, and the rest observe it hidden -> exactly one claim.
        QueueServiceDataPlane.OnDequeueClaim = () =>
        {
            Interlocked.Increment(ref claims);
            if (Interlocked.Increment(ref arrived) == dequeuers)
            {
                allArrived.Set();
            }

            allArrived.Wait(TimeSpan.FromSeconds(3));
        };

        try
        {
            var tasks = Enumerable.Range(0, dequeuers).Select(_ => Task.Run(() =>
            {
                try
                {
                    // 30s dequeue visibility: the first successful claimer hides the message for the window.
                    dataPlane.GetMessages(subscription, resourceGroup, account, queue,
                        numMessages: 1, visibilityTimeout: 30);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            })).ToArray();

            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), Is.True,
                "precondition: all concurrent dequeues completed (none hung)");
        }
        finally
        {
            QueueServiceDataPlane.OnDequeueClaim = null;
        }

        Assert.That(errors, Is.Empty,
            "no dequeue may throw; first error: " + (errors.FirstOrDefault()?.ToString() ?? "<none>"));

        // The invariant: the one message is claimed by exactly one of the concurrent callers.
        Assert.That(claims, Is.EqualTo(1),
            $"the single message must be claimed exactly once across {dequeuers} concurrent GetMessages calls, "
            + $"but {claims} callers claimed it - concurrent dequeue is not atomic.");
    }

    /// <summary>
    /// Coverage of the message-file durability invariant: concurrent writers to the SAME message file must never
    /// corrupt it. <see cref="QueueServiceDataPlane.GetMessages"/> rewrites a claimed message (DequeueCount +
    /// NextVisibleTime) and <see cref="QueueServiceDataPlane.PutMessage"/> rewrites it again on a visibility
    /// update (the job-dispatch watchdog re-extends an in-flight trigger this way). Pre-fix both did a non-atomic,
    /// unserialized File.WriteAllText (truncate-then-write) on the same path, so the two interleaved into invalid
    /// JSON. A corrupted file then fails to deserialize on every later dequeue and is silently skipped, so the
    /// message - e.g. a queued job message - is stranded forever and the deployment wedges with
    /// "artifacts not registered" (observed on-disk: an 801-byte message file with a trailing extra '}').
    ///
    /// <para>The fix serializes all message mutations under the per-queue lock and writes atomically (temp file +
    /// rename), so a reader/writer only ever observes a COMPLETE file. This test hammers one message with
    /// concurrent dequeue + visibility-update and asserts it stays valid JSON and dequeueable.</para>
    /// </summary>
    [Test]
    public void ConcurrentDequeueAndVisibilityUpdate_NeverCorruptTheMessageFile()
    {
        var logger = new PrettyTopazLogger();
        var dataPlane = QueueServiceDataPlane.New(logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string queue = "tornwritequeue";

        var create = dataPlane.CreateQueue(subscription, resourceGroup, account, queue);
        Assert.That(create.Result, Is.EqualTo(OperationResult.Created).Or.EqualTo(OperationResult.Success),
            "precondition: queue created");

        var send = dataPlane.SendMessage(subscription, resourceGroup, account, queue, "payload", visibilityTimeout: 0);
        Assert.That(send.Result, Is.EqualTo(OperationResult.Success), "precondition: single message enqueued");
        var messageId = send.Resource!.Id!;

        // Churn the SAME message file with concurrent visibility-updates (watchdog path) and dequeues (writeback).
        const int threads = 6;
        const int rounds = 300;
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                try
                {
                    // Visibility-only update (no body): the in-flight-trigger watchdog re-extend.
                    dataPlane.PutMessage(subscription, resourceGroup, account, queue, messageId,
                        content: "", visibilityTimeout: 1);
                    // Re-dequeue with a zero lease so the message stays immediately visible and keeps cycling.
                    dataPlane.GetMessages(subscription, resourceGroup, account, queue,
                        numMessages: 1, visibilityTimeout: 0);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        })).ToArray();

        Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)), Is.True,
            "precondition: all churn threads completed (none hung)");

        Assert.That(errors, Is.Empty,
            "no dequeue/visibility-update may throw - a torn message file fails to deserialize; first error: "
            + (errors.FirstOrDefault()?.ToString() ?? "<none>"));

        // The message must not be stranded: a corrupted, unparseable file would make this update throw on
        // deserialize, or make the dequeue below silently skip it.
        var makeVisible = dataPlane.PutMessage(subscription, resourceGroup, account, queue, messageId,
            content: "", visibilityTimeout: 0);
        Assert.That(makeVisible.Result, Is.EqualTo(OperationResult.Success),
            "the message file must still be valid JSON after concurrent churn (a torn write would fail here)");

        var finalGet = dataPlane.GetMessages(subscription, resourceGroup, account, queue,
            numMessages: 1, visibilityTimeout: 30);
        Assert.That(finalGet.Result, Is.EqualTo(OperationResult.Success), "final dequeue succeeds");
        Assert.That(finalGet.Resource!.Count, Is.EqualTo(1),
            "the message must remain dequeueable after concurrent dequeue + visibility-update churn - a corrupted, "
            + "unparseable file would be silently skipped by GetMessages and the job stranded forever.");
    }

    /// <summary>
    /// Coverage of the enqueue-vs-dequeue durability race: <see cref="QueueServiceDataPlane.SendMessage"/> creates
    /// a NEW message file while <see cref="QueueServiceDataPlane.GetMessages"/> is mid-scan over the same
    /// directory. Pre-fix SendMessage's non-atomic File.WriteAllText could be observed half-written by a
    /// concurrent dequeue scan (a partial file deserializes to null/garbage and is dropped), silently LOSING the
    /// just-enqueued message. The atomic temp-file+rename makes a new message appear to readers in one step, so a
    /// scan sees either the complete message or nothing - never a torn file. Asserts every enqueued message is
    /// still retrievable after concurrent enqueue + dequeue pressure.
    /// </summary>
    [Test]
    public void ConcurrentSendAndDequeue_NeverLoseOrCorruptMessages()
    {
        var logger = new PrettyTopazLogger();
        var dataPlane = QueueServiceDataPlane.New(logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string queue = "sendracequeue";

        var create = dataPlane.CreateQueue(subscription, resourceGroup, account, queue);
        Assert.That(create.Result, Is.EqualTo(OperationResult.Created).Or.EqualTo(OperationResult.Success),
            "precondition: queue created");

        const int producers = 4;
        const int perProducer = 25;
        const int totalSent = producers * perProducer; // 100
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var stop = new ManualResetEventSlim(false);

        // Consumers create read/write pressure on the directory while producers add files. They dequeue with a
        // zero lease (message stays immediately visible) and never delete, so nothing is consumed away - the
        // count check below is purely about enqueue durability under a concurrent scan.
        var consumers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsSet)
            {
                try { dataPlane.GetMessages(subscription, resourceGroup, account, queue, numMessages: 32, visibilityTimeout: 0); }
                catch (Exception ex) { errors.Add(ex); }
            }
        })).ToArray();

        var sentIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        var produceTasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                try
                {
                    var r = dataPlane.SendMessage(subscription, resourceGroup, account, queue,
                        $"msg-{p}-{i}", visibilityTimeout: 0);
                    if (r.Result == OperationResult.Success) { sentIds.Add(r.Resource!.Id!); }
                    else { errors.Add(new Exception("SendMessage failed: " + r.Code)); }
                }
                catch (Exception ex) { errors.Add(ex); }
            }
        })).ToArray();

        Assert.That(Task.WaitAll(produceTasks, TimeSpan.FromSeconds(60)), Is.True, "precondition: all producers finished");
        stop.Set();
        Task.WaitAll(consumers, TimeSpan.FromSeconds(10));

        Assert.That(errors, Is.Empty,
            "no enqueue/dequeue may throw under concurrency; first error: " + (errors.FirstOrDefault()?.ToString() ?? "<none>"));
        Assert.That(sentIds.Count, Is.EqualTo(totalSent), "precondition: every SendMessage reported success");

        // Drain everything (all immediately visible) and confirm not one enqueued message was lost or corrupted.
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 20 && distinct.Count < totalSent; attempt++)
        {
            var got = dataPlane.GetMessages(subscription, resourceGroup, account, queue, numMessages: totalSent, visibilityTimeout: 0);
            Assert.That(got.Result, Is.EqualTo(OperationResult.Success), "drain dequeue succeeds");
            foreach (var m in got.Resource!) { distinct.Add(m.Id!); }
        }

        Assert.That(distinct.Count, Is.EqualTo(totalSent),
            $"all {totalSent} enqueued messages must survive a concurrent dequeue scan, but only {distinct.Count} "
            + "are retrievable - a half-written new message file was dropped by the scan (enqueue not atomic).");
    }

    /// <summary>
    /// Coverage of the delete-vs-visibility-update race the PutMessage comment calls out: a consumer deletes a
    /// finished message while a late in-flight visibility update (the dispatch watchdog) targets the same id.
    /// Pre-fix PutMessage's lock-free File.Exists -> ReadAllText -> File.WriteAllText could straddle the delete
    /// and RESURRECT the message (often with empty content), which is then handed back on every dequeue and breaks
    /// body-parsing consumers. Under the per-queue lock the two serialize: a delete that already removed the file
    /// makes the update report MessageNotFound, never resurrecting it. Asserts a raced message stays deleted.
    /// </summary>
    [Test]
    public void ConcurrentDeleteAndVisibilityUpdate_NeverResurrectTheMessage()
    {
        var logger = new PrettyTopazLogger();
        var dataPlane = QueueServiceDataPlane.New(logger);

        var subscription = SubscriptionIdentifier.From(Guid.NewGuid());
        var resourceGroup = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string queue = "deleteracequeue";

        var create = dataPlane.CreateQueue(subscription, resourceGroup, account, queue);
        Assert.That(create.Result, Is.EqualTo(OperationResult.Created).Or.EqualTo(OperationResult.Success),
            "precondition: queue created");

        const int rounds = 100;
        var resurrected = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        for (var i = 0; i < rounds; i++)
        {
            var send = dataPlane.SendMessage(subscription, resourceGroup, account, queue, "payload-" + i, visibilityTimeout: 0);
            Assert.That(send.Result, Is.EqualTo(OperationResult.Success), "precondition: message enqueued");
            var messageId = send.Resource!.Id!;

            // Race a delete against a visibility-only update on the same message.
            var del = Task.Run(() =>
            {
                try { dataPlane.DeleteMessage(subscription, resourceGroup, account, queue, messageId); }
                catch (Exception ex) { errors.Add(ex); }
            });
            var upd = Task.Run(() =>
            {
                try { dataPlane.PutMessage(subscription, resourceGroup, account, queue, messageId, content: "", visibilityTimeout: 0); }
                catch (Exception ex) { errors.Add(ex); }
            });
            Task.WaitAll([del, upd], TimeSpan.FromSeconds(10));

            // The deleted message must NOT be resurrected by the racing update.
            var peek = dataPlane.GetMessages(subscription, resourceGroup, account, queue, numMessages: 5, visibilityTimeout: 0);
            if (peek.Result == OperationResult.Success && peek.Resource!.Count > 0)
            {
                resurrected++;
                // Clean up so the next round starts empty.
                foreach (var m in peek.Resource!)
                {
                    dataPlane.DeleteMessage(subscription, resourceGroup, account, queue, m.Id!);
                }
            }
        }

        Assert.That(errors, Is.Empty,
            "no delete/update may throw under concurrency; first error: " + (errors.FirstOrDefault()?.ToString() ?? "<none>"));
        Assert.That(resurrected, Is.EqualTo(0),
            $"a deleted message must never be resurrected by a racing visibility update, but it was in {resurrected}/"
            + $"{rounds} rounds - delete and update are not serialized.");
    }
}
