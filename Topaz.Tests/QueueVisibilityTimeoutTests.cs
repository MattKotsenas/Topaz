using NUnit.Framework;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Get Messages (dequeue) must honour Azure Storage Queue's visibility-timeout floor of 1 second. Real Azure
/// rejects a Get Messages <c>visibilitytimeout</c> of 0 (or any value below 1s) with 400
/// OutOfRangeQueryParameterValue - even though Put/Update Message permit 0. Topaz used to silently accept 0,
/// which let the consuming emulator run on an invalid "lease=0" dequeue config that re-delivers every message
/// immediately (dup-execution). These tests pin the floor so the invalid config fails fast, exactly as it does
/// against real Azure, instead of leaving the emulator on a foundation of sand.
/// </summary>
[TestFixture]
public class QueueVisibilityTimeoutTests
{
    private static (QueueServiceDataPlane dataPlane, SubscriptionIdentifier sub, ResourceGroupIdentifier rg, string account, string queue) NewQueue()
    {
        var logger = new PrettyTopazLogger();
        var dataPlane = QueueServiceDataPlane.New(logger);
        var sub = SubscriptionIdentifier.From(Guid.NewGuid());
        var rg = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        var queue = "vtq" + Guid.NewGuid().ToString("N")[..8];

        var create = dataPlane.CreateQueue(sub, rg, account, queue);
        Assert.That(create.Result, Is.EqualTo(OperationResult.Created).Or.EqualTo(OperationResult.Success),
            "precondition: queue created");
        return (dataPlane, sub, rg, account, queue);
    }

    [TestCase(0)]   // the invalid "lease=0" the emulator used to rely on
    [TestCase(-1)]
    [TestCase(-30)]
    public void GetMessages_RejectsVisibilityTimeoutBelowOneSecond(int visibilityTimeout)
    {
        Assert.That(visibilityTimeout, Is.LessThan(1), "precondition: the test value is below the 1s Get Messages floor");
        var (dataPlane, sub, rg, account, queue) = NewQueue();

        var result = dataPlane.GetMessages(sub, rg, account, queue, numMessages: 1, visibilityTimeout: visibilityTimeout);

        Assert.That(result.Result, Is.EqualTo(OperationResult.Failed),
            "Get Messages with a visibility timeout below 1s must be rejected, like real Azure Storage Queue.");
        Assert.That(result.Code, Is.EqualTo("OutOfRangeQueryParameterValue"),
            "the rejection must use Azure's OutOfRangeQueryParameterValue error code.");
    }

    [TestCase(1)]      // boundary: the minimum Azure permits
    [TestCase(2)]      // the daemon's committed exactly-once lease
    [TestCase(30)]     // Azure's default
    [TestCase(604800)] // boundary: 7 days, the maximum
    public void GetMessages_AcceptsVisibilityTimeoutOfOneSecondAndAbove(int visibilityTimeout)
    {
        Assert.That(visibilityTimeout, Is.GreaterThanOrEqualTo(1), "precondition: the test value is at/above the 1s floor");
        var (dataPlane, sub, rg, account, queue) = NewQueue();
        // Enqueue is allowed to use 0 (Put Message permits it); the message is immediately visible.
        dataPlane.SendMessage(sub, rg, account, queue, "hello", visibilityTimeout: 0);

        var result = dataPlane.GetMessages(sub, rg, account, queue, numMessages: 1, visibilityTimeout: visibilityTimeout);

        Assert.That(result.Result, Is.EqualTo(OperationResult.Success),
            $"Get Messages with a valid {visibilityTimeout}s visibility timeout must succeed.");
        Assert.That(result.Resource!.Count, Is.EqualTo(1), "the enqueued message must be returned.");
    }
}
