using NUnit.Framework;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage;
using Topaz.Service.Storage.Models;
using Topaz.Shared;

namespace Topaz.Tests;

/// <summary>
/// Direct, host-free coverage of the queue pop-receipt invariant: Azure regenerates a message's pop receipt on
/// every dequeue (Get Messages) and every update, and Delete/Update Message must present the CURRENT receipt. A
/// stale one - e.g. the message was re-dequeued by another consumer after its visibility timeout - is rejected
/// with 400 PopReceiptMismatch. Topaz previously ignored the receipt entirely, so a worker whose lease had lapsed
/// could still delete/update a trigger another worker now owned - breaking the exactly-once claim a distributed
/// job dispatcher relies on. A missing message stays 404 MessageNotFound, distinct from the 400 mismatch.
/// </summary>
[TestFixture]
[NonParallelizable]
public class QueuePopReceiptValidationTests
{
    private static (QueueServiceDataPlane dataPlane, SubscriptionIdentifier sub, ResourceGroupIdentifier rg,
        string account, string queue) NewQueue()
    {
        var logger = new PrettyTopazLogger();
        var dataPlane = QueueServiceDataPlane.New(logger);
        var sub = SubscriptionIdentifier.From(Guid.NewGuid());
        var rg = ResourceGroupIdentifier.From("rg-" + Guid.NewGuid().ToString("N")[..8]);
        const string account = "acct";
        const string queue = "popreceiptqueue";
        var create = dataPlane.CreateQueue(sub, rg, account, queue);
        Assert.That(create.Result, Is.EqualTo(OperationResult.Created).Or.EqualTo(OperationResult.Success),
            "precondition: queue created");
        return (dataPlane, sub, rg, account, queue);
    }

    private static QueueMessage SendAndDequeue(QueueServiceDataPlane dp, SubscriptionIdentifier sub,
        ResourceGroupIdentifier rg, string account, string queue)
    {
        dp.SendMessage(sub, rg, account, queue, "hello", visibilityTimeout: 0);
        var got = dp.GetMessages(sub, rg, account, queue, numMessages: 1, visibilityTimeout: 30);
        Assert.That(got.Result, Is.EqualTo(OperationResult.Success));
        Assert.That(got.Resource, Is.Not.Null.And.Count.EqualTo(1), "precondition: exactly one message dequeued");
        var msg = got.Resource![0];
        Assert.That(msg.Id, Is.Not.Null.And.Not.Empty);
        Assert.That(msg.PopReceipt, Is.Not.Null.And.Not.Empty, "precondition: a dequeue assigns a pop receipt");
        return msg;
    }

    [Test]
    public void DeleteMessage_WithMatchingPopReceipt_Succeeds()
    {
        var (dp, sub, rg, account, queue) = NewQueue();
        var msg = SendAndDequeue(dp, sub, rg, account, queue);

        var del = dp.DeleteMessage(sub, rg, account, queue, msg.Id!, msg.PopReceipt);

        Assert.That(del.Result, Is.EqualTo(OperationResult.Success));
    }

    [Test]
    public void DeleteMessage_WithMismatchedPopReceipt_IsRejected_AndMessagePreserved()
    {
        var (dp, sub, rg, account, queue) = NewQueue();
        var msg = SendAndDequeue(dp, sub, rg, account, queue);
        Assert.That(msg.PopReceipt, Is.Not.EqualTo("stale-receipt"), "precondition: the stale receipt differs from the real one");

        var rejected = dp.DeleteMessage(sub, rg, account, queue, msg.Id!, "stale-receipt");
        Assert.That(rejected.Result, Is.EqualTo(OperationResult.BadRequest));
        Assert.That(rejected.Code, Is.EqualTo("PopReceiptMismatch"));

        // A rejected claim must leave the message intact for its rightful owner.
        var deletedWithReal = dp.DeleteMessage(sub, rg, account, queue, msg.Id!, msg.PopReceipt);
        Assert.That(deletedWithReal.Result, Is.EqualTo(OperationResult.Success),
            "the real receipt still deletes the preserved message");
    }

    [Test]
    public void UpdateMessage_WithMismatchedPopReceipt_IsRejected()
    {
        var (dp, sub, rg, account, queue) = NewQueue();
        var msg = SendAndDequeue(dp, sub, rg, account, queue);

        var rejected = dp.PutMessage(sub, rg, account, queue, msg.Id!, content: "", visibilityTimeout: 30,
            popReceipt: "stale-receipt");

        Assert.That(rejected.Result, Is.EqualTo(OperationResult.BadRequest));
        Assert.That(rejected.Code, Is.EqualTo("PopReceiptMismatch"));
    }

    [Test]
    public void UpdateMessage_RegeneratesReceipt_SoThePriorReceiptIsThenRejected()
    {
        var (dp, sub, rg, account, queue) = NewQueue();
        var msg = SendAndDequeue(dp, sub, rg, account, queue);
        var firstReceipt = msg.PopReceipt!;

        // An update with the current receipt succeeds AND regenerates the receipt (Azure semantics).
        var updated = dp.PutMessage(sub, rg, account, queue, msg.Id!, content: "", visibilityTimeout: 30,
            popReceipt: firstReceipt);
        Assert.That(updated.Result, Is.EqualTo(OperationResult.Success));
        Assert.That(updated.Resource!.PopReceipt, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(firstReceipt),
            "an update regenerates the pop receipt");
        var secondReceipt = updated.Resource!.PopReceipt!;

        // The now-stale first receipt must be rejected - the re-claim a lapsed-lease worker hits.
        var staleDelete = dp.DeleteMessage(sub, rg, account, queue, msg.Id!, firstReceipt);
        Assert.That(staleDelete.Result, Is.EqualTo(OperationResult.BadRequest));
        Assert.That(staleDelete.Code, Is.EqualTo("PopReceiptMismatch"));

        var freshDelete = dp.DeleteMessage(sub, rg, account, queue, msg.Id!, secondReceipt);
        Assert.That(freshDelete.Result, Is.EqualTo(OperationResult.Success));
    }

    [Test]
    public void DeleteMessage_NonExistentMessage_IsMessageNotFound_NotMismatch()
    {
        var (dp, sub, rg, account, queue) = NewQueue();

        var del = dp.DeleteMessage(sub, rg, account, queue, Guid.Empty.ToString(), "any-receipt");

        Assert.That(del.Result, Is.EqualTo(OperationResult.NotFound));
        Assert.That(del.Code, Is.EqualTo("MessageNotFound"),
            "a missing message is 404 MessageNotFound, distinct from a 400 PopReceiptMismatch");
    }
}
