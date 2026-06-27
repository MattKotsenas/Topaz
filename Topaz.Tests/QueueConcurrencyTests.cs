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
}
