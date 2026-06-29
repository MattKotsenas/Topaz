using System.Text.Json;
using Topaz.Service.Shared;
using Topaz.Service.Shared.Domain;
using Topaz.Service.Storage.Models;
using Topaz.Service.Storage.Utils;
using Topaz.Shared;

namespace Topaz.Service.Storage;

internal sealed class QueueServiceDataPlane(QueueServiceControlPlane controlPlane, QueueResourceProvider resourceProvider, ITopazLogger logger)
{
    /// <summary>
    /// Test seam: invoked when a <see cref="GetMessages"/> caller has CLAIMED a message - i.e. passed the
    /// visibility check and is about to take it - before the visibility writeback. Null (and zero-cost) in
    /// production. It lets a concurrency test deterministically observe how many callers claim the SAME message,
    /// which is the platform-independent signal for the dequeue-atomicity invariant: OS file locking masks the
    /// write-back race on Windows (a losing writer throws and is swallowed) but not on Linux (the container,
    /// where the duplicate dequeue actually happens), so asserting on returned counts alone is unreliable.
    /// </summary>
    internal static Action? OnDequeueClaim;

    public static QueueServiceDataPlane New(ITopazLogger logger)
    {
        var resourceProvider = new QueueResourceProvider(logger);
        var controlPlane = QueueServiceControlPlane.New(logger);
        return new QueueServiceDataPlane(controlPlane, resourceProvider, logger);
    }
    public DataPlaneOperationResult<QueueEnumerationResult> ListQueues(
        SubscriptionIdentifier subscriptionIdentifier, ResourceGroupIdentifier resourceGroupIdentifier,
        string storageAccountName)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(ListQueues),
            "Executing {0}: {1}", nameof(ListQueues), storageAccountName);

        var result = controlPlane.ListQueues(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName);
        if (result.Result == OperationResult.Success && result.Resource != null)
        {
            var queueEnumeration = new QueueEnumerationResult(storageAccountName, result.Resource);
            return new DataPlaneOperationResult<QueueEnumerationResult>(OperationResult.Success, queueEnumeration,
                null, null);
        }

        return new DataPlaneOperationResult<QueueEnumerationResult>(OperationResult.Failed, null,
            "Failed to list queues.", null);
    }

    public DataPlaneOperationResult<Queue> CreateQueue(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(CreateQueue),
            "Executing {0}: {1} {2}", nameof(CreateQueue), storageAccountName, queueName);

        if (controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName,
                queueName))
        {
            return new DataPlaneOperationResult<Queue>(OperationResult.Conflict, null,
                "Queue already exists.", "QueueAlreadyExists");
        }

        var result = controlPlane.CreateQueue(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        return new DataPlaneOperationResult<Queue>(result.Result, result.Resource, result.Reason,
            result.Code);
    }

    public DataPlaneOperationResult DeleteQueue(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(DeleteQueue),
            "Executing {0}: {1} {2}", nameof(DeleteQueue), storageAccountName, queueName);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName,
                queueName))
        {
            return new DataPlaneOperationResult(OperationResult.NotFound, "Queue not found.", "QueueNotFound");
        }

        var result = controlPlane.DeleteQueue(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        return new DataPlaneOperationResult(result.Result, result.Reason, result.Code);
    }

    public DataPlaneOperationResult<QueueProperties> GetQueueProperties(
        SubscriptionIdentifier subscriptionIdentifier, ResourceGroupIdentifier resourceGroupIdentifier,
        string storageAccountName, string queueName)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(GetQueueProperties),
            "Executing {0}: {1} {2}", nameof(GetQueueProperties), storageAccountName, queueName);

        var result = controlPlane.GetQueueProperties(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        if (result.Result == OperationResult.Success && result.Resource != null)
        {
            var messagesDir = resourceProvider.GetMessagesDirectoryPath(
                subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName);
            result.Resource.ApproximateMessageCount = Directory.Exists(messagesDir)
                ? Directory.GetFiles(messagesDir, "*.json").Length
                : 0;
        }

        return new DataPlaneOperationResult<QueueProperties>(result.Result, result.Resource, result.Reason,
            result.Code);
    }

    public DataPlaneOperationResult<string> GetQueueAcl(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(GetQueueAcl),
            "Executing {0}: {1} {2}", nameof(GetQueueAcl), storageAccountName, queueName);

        var (exists, aclFilePath) = controlPlane.GetQueueAclState(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        if (!exists)
            return new DataPlaneOperationResult<string>(OperationResult.NotFound, null, "Queue not found.", "QueueNotFound");

        if (!File.Exists(aclFilePath))
            return new DataPlaneOperationResult<string>(OperationResult.Success,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers />", null, null);

        var xml = File.ReadAllText(aclFilePath);
        return new DataPlaneOperationResult<string>(OperationResult.Success, xml, null, null);
    }

    public DataPlaneOperationResult SetQueueAcl(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName,
        Stream requestBody)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(SetQueueAcl),
            "Executing {0}: {1} {2}", nameof(SetQueueAcl), storageAccountName, queueName);

        var (exists, aclFilePath) = controlPlane.GetQueueAclState(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        if (!exists)
            return new DataPlaneOperationResult(OperationResult.NotFound, "Queue not found.", "QueueNotFound");

        using var reader = new StreamReader(requestBody);
        var body = reader.ReadToEnd();

        if (string.IsNullOrWhiteSpace(body))
            body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers />";

        File.WriteAllText(aclFilePath, body);
        return new DataPlaneOperationResult(OperationResult.Success, null, null);
    }

    public DataPlaneOperationResult SetQueueMetadata(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName,
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> headers)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(SetQueueMetadata),
            "Executing {0}: {1} {2}", nameof(SetQueueMetadata), storageAccountName, queueName);

        var metadata = headers
            .Where(h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key["x-ms-meta-".Length..], h => h.Value.ToString());

        var result = controlPlane.SetQueueMetadata(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName, metadata);

        return new DataPlaneOperationResult(result.Result, result.Reason, result.Code);
    }

    public DataPlaneOperationResult<QueueMessage> PutMessage(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName,
        string messageId, string content, int visibilityTimeout = 30, string? popReceipt = null)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(PutMessage),
            "Executing {0}: {1} {2} {3}", nameof(PutMessage), storageAccountName, queueName, messageId);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.NotFound, null,
                "Queue not found.", "QueueNotFound");
        }

        var messageDir = resourceProvider.GetMessagesDirectoryPath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);
        Directory.CreateDirectory(messageDir);

        var messagePath = resourceProvider.GetMessageFilePath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName, messageId);

        // Read-modify-write under the per-queue lock + atomic temp-file rename, serialized with the
        // GetMessages dequeue visibility-writeback and with DeleteMessage. Plain File.WriteAllText is
        // truncate-then-write: neither atomic nor mutually exclusive, so a watchdog visibility-update racing a
        // concurrent dequeue-writeback on the SAME message file interleaves into invalid JSON. That corrupted
        // file then fails to deserialize on every later dequeue and strands the message (and its job) forever -
        // the foundation bug behind the "queued trigger created but never executes" processing stall. The
        // lock plus the rename close the window and match Azure update/delete atomicity.
        lock (QueueLock(messageDir))
        {
            if (!File.Exists(messagePath))
            {
                // Azure Queue Storage's Update Message returns 404 MessageNotFound when the target message no
                // longer exists (it was already dequeued-and-deleted by another consumer). Resurrecting it here
                // would hand back an EMPTY-content message on every subsequent dequeue and break any consumer
                // that parses the message body. Match Azure: report not-found.
                logger.LogDebug(nameof(QueueServiceDataPlane), nameof(PutMessage),
                    "Update for non-existent message {0} in queue {1}; returning MessageNotFound.", messageId, queueName);
                return new DataPlaneOperationResult<QueueMessage>(OperationResult.NotFound, null,
                    "Message not found.", "MessageNotFound");
            }

            var existingContent = File.ReadAllText(messagePath);
            var message = JsonSerializer.Deserialize<QueueMessage>(existingContent, GlobalSettings.JsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize message");

            // Azure regenerates a message's pop receipt on every dequeue (Get Messages) and every update, and an
            // Update Message must present the CURRENT receipt. A stale one - e.g. the message was re-dequeued by
            // another consumer after its visibility timeout expired - is rejected with 400 PopReceiptMismatch. This
            // is the optimistic claim a losing consumer hits, and the property a distributed job dispatcher relies
            // on so a trigger isn't updated/re-claimed by a worker whose lease has already lapsed.
            if (!string.IsNullOrEmpty(popReceipt) && !PopReceiptMatches(message, popReceipt))
            {
                return new DataPlaneOperationResult<QueueMessage>(OperationResult.BadRequest, null,
                    "The specified pop receipt did not match the pop receipt for a dequeued message.", "PopReceiptMismatch");
            }

            // A visibility-only update sends no request body, so the content arrives empty; it must
            // preserve the existing message content. Only overwrite the content when new content was
            // actually provided (Azure's update-message only changes content when the request carries one).
            if (!string.IsNullOrEmpty(content))
            {
                message.UpdateContent(content);
            }
            message.UpdateVisibility(visibilityTimeout);

            WriteMessageFileAtomic(messagePath, JsonSerializer.Serialize(message, GlobalSettings.JsonOptions));

            return new DataPlaneOperationResult<QueueMessage>(OperationResult.Success, message, null, null);
        }
    }

    public DataPlaneOperationResult<QueueMessage> GetMessage(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName, string messageId)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(GetMessage),
            "Executing {0}: {1} {2} {3}", nameof(GetMessage), storageAccountName, queueName, messageId);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.NotFound, null,
                "Queue not found.", "QueueNotFound");
        }

        var messagePath = resourceProvider.GetMessageFilePath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName, messageId);

        if (!File.Exists(messagePath))
        {
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.NotFound, null,
                "Message not found.", "MessageNotFound");
        }

        var messageContent = File.ReadAllText(messagePath);
        var message = JsonSerializer.Deserialize<QueueMessage>(messageContent, GlobalSettings.JsonOptions);

        if (message == null)
        {
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.Failed, null,
                "Failed to deserialize message.", "DeserializationError");
        }

        // Check if message has expired
        if (message.IsExpired())
        {
            File.Delete(messagePath);
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.NotFound, null,
                "Message has expired.", "MessageExpired");
        }

        return new DataPlaneOperationResult<QueueMessage>(OperationResult.Success, message, null, null);
    }

    public DataPlaneOperationResult ClearMessages(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(ClearMessages),
            "Executing {0}: {1} {2}", nameof(ClearMessages), storageAccountName, queueName);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult(OperationResult.NotFound, "Queue not found.", "QueueNotFound");
        }

        var messageDir = resourceProvider.GetMessagesDirectoryPath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        if (Directory.Exists(messageDir))
        {
            foreach (var file in Directory.GetFiles(messageDir, "*.json"))
                File.Delete(file);
        }

        return new DataPlaneOperationResult(OperationResult.Success, null, null);
    }

    public DataPlaneOperationResult DeleteMessage(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName, string messageId,
        string? popReceipt = null)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(DeleteMessage),
            "Executing {0}: {1} {2} {3}", nameof(DeleteMessage), storageAccountName, queueName, messageId);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult(OperationResult.NotFound,
                "Queue not found.", "QueueNotFound");
        }

        var messageDir = resourceProvider.GetMessagesDirectoryPath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);
        var messagePath = resourceProvider.GetMessageFilePath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName, messageId);

        // Serialized with the dequeue visibility-writeback (GetMessages) and PutMessage on this queue, so a
        // delete cannot interleave with a visibility update and resurrect or corrupt the message file.
        lock (QueueLock(messageDir))
        {
            if (!File.Exists(messagePath))
            {
                return new DataPlaneOperationResult(OperationResult.NotFound,
                    "Message not found.", "MessageNotFound");
            }

            // Azure requires the caller's pop receipt to match the message's CURRENT receipt (regenerated on every
            // dequeue/update). A stale receipt - e.g. the message was re-dequeued by another consumer after its
            // visibility timeout - is rejected with 400 PopReceiptMismatch, which is how exactly-once delete is
            // enforced: a worker whose lease lapsed cannot delete a trigger another worker now owns.
            if (!string.IsNullOrEmpty(popReceipt))
            {
                var message = JsonSerializer.Deserialize<QueueMessage>(File.ReadAllText(messagePath),
                    GlobalSettings.JsonOptions);
                if (message is null || !PopReceiptMatches(message, popReceipt))
                {
                    return new DataPlaneOperationResult(OperationResult.BadRequest,
                        "The specified pop receipt did not match the pop receipt for a dequeued message.", "PopReceiptMismatch");
                }
            }

            File.Delete(messagePath);
        }

        return new DataPlaneOperationResult(OperationResult.Success, null, null);
    }

    // A message's pop receipt is regenerated on every dequeue (Get Messages) and every update; Delete/Update must
    // present the current one. Ordinal comparison matches the opaque-token semantics Azure uses.
    private static bool PopReceiptMatches(QueueMessage message, string popReceipt) =>
        string.Equals(message.PopReceipt, popReceipt, StringComparison.Ordinal);

    public DataPlaneOperationResult<QueueMessage> PeekMessage(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName, string messageId)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(PeekMessage),
            "Executing {0}: {1} {2} {3}", nameof(PeekMessage), storageAccountName, queueName, messageId);

        // Peek is similar to Get but without incrementing dequeue count or affecting visibility
        return GetMessage(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName, messageId);
    }

    public DataPlaneOperationResult<List<QueueMessage>> GetMessages(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName,
        int numMessages = 1, int visibilityTimeout = 30)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(GetMessages),
            "Executing {0}: {1} {2} numMessages={3} visibilityTimeout={4}", 
            nameof(GetMessages), storageAccountName, queueName, numMessages, visibilityTimeout);

        // Azure Storage Queue's Get Messages requires a visibility timeout of 1s..7d (default 30s) - it rejects 0.
        // Enforce that floor here (defence-in-depth with the endpoint) so an invalid lease=0 dequeue fails fast,
        // matching real Azure, instead of being silently accepted (which is what let the consuming emulator run on an
        // invalid, dup-producing config). Put/Update Message still allow 0, so this guard is on Get Messages only.
        if (!QueueMessageValidator.ValidateGetMessagesVisibilityTimeout(visibilityTimeout, out var visibilityError))
        {
            logger.LogDebug(nameof(QueueServiceDataPlane), nameof(GetMessages),
                "Get Messages visibility timeout validation failed: {0}", visibilityError);
            return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.Failed, null,
                visibilityError, "OutOfRangeQueryParameterValue");
        }

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.NotFound, null,
                "Queue not found.", "QueueNotFound");
        }

        var messageDir = resourceProvider.GetMessagesDirectoryPath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        if (!Directory.Exists(messageDir))
        {
            return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.Success, 
                new List<QueueMessage>(), null, null);
        }

        var messages = new List<QueueMessage>();

        // Serialize the dequeue scan per queue. The store is file-backed and a dequeue is a read-check-modify-
        // write (read file -> IsVisible? -> bump DequeueCount + set NextVisibleTime -> write). Without this lock,
        // concurrent callers each read a message as still-visible before any of them writes the hidden
        // NextVisibleTime back, so they ALL claim and return it - the at-least-once queue hands one job trigger
        // to every concurrent worker at once (the 500x setup burst behind the never-dispatches stall).
        // Holding a per-queue lock across the scan makes the visibility update atomic: the first claimer hides
        // the message (for visibilityTimeout) and the rest observe it hidden and skip - at most one dequeuer per
        // visibility window, matching Azure Storage Queue semantics. Mirrors TableServiceDataPlane's per-entity
        // lock; see QueueConcurrencyTests for the deterministic red->green proof.
        lock (QueueLock(messageDir))
        {
            var messageFiles = Directory.GetFiles(messageDir, "*.json").OrderBy(f => f).ToArray();

            foreach (var filePath in messageFiles)
            {
                if (messages.Count >= numMessages)
                    break;

                try
                {
                    var messageContent = File.ReadAllText(filePath);
                    var message = JsonSerializer.Deserialize<QueueMessage>(messageContent, GlobalSettings.JsonOptions);

                    if (message == null)
                        continue;

                    // Skip if expired
                    if (message.IsExpired())
                    {
                        File.Delete(filePath);
                        continue;
                    }

                    // Skip if not visible yet
                    if (!message.IsVisible())
                        continue;

                    // This caller has claimed a visible, unexpired message (test seam; no-op in production).
                    OnDequeueClaim?.Invoke();

                    // Message is visible and not expired - prepare for return
                    message.DequeueCount++;
                    message.UpdateVisibility(visibilityTimeout);

                    // Persist the updated message (visibility writeback) under the per-queue lock,
                    // atomically (temp file + rename) so a concurrent reader/writer never sees a torn file.
                    WriteMessageFileAtomic(filePath, JsonSerializer.Serialize(message, GlobalSettings.JsonOptions));

                    messages.Add(message);
                }
                catch (Exception ex)
                {
                    logger.LogError(nameof(QueueServiceDataPlane), nameof(GetMessages),
                        "Error processing message file {0}: {1}", filePath, ex.Message);
                }
            }
        }

        return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.Success, messages, null, null);
    }

    public DataPlaneOperationResult<List<QueueMessage>> PeekMessages(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName,
        int numMessages = 1)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(PeekMessages),
            "Executing {0}: {1} {2} numMessages={3}",
            nameof(PeekMessages), storageAccountName, queueName, numMessages);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.NotFound, null,
                "Queue not found.", "QueueNotFound");
        }

        var messageDir = resourceProvider.GetMessagesDirectoryPath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);

        if (!Directory.Exists(messageDir))
        {
            return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.Success,
                new List<QueueMessage>(), null, null);
        }

        var messages = new List<QueueMessage>();
        var messageFiles = Directory.GetFiles(messageDir, "*.json").OrderBy(f => f).ToArray();

        foreach (var filePath in messageFiles)
        {
            if (messages.Count >= numMessages)
                break;

            try
            {
                var messageContent = File.ReadAllText(filePath);
                var message = JsonSerializer.Deserialize<QueueMessage>(messageContent, GlobalSettings.JsonOptions);

                if (message == null)
                    continue;

                if (message.IsExpired())
                {
                    File.Delete(filePath);
                    continue;
                }

                if (!message.IsVisible())
                    continue;

                messages.Add(message);
            }
            catch (Exception ex)
            {
                logger.LogError(nameof(QueueServiceDataPlane), nameof(PeekMessages),
                    "Error processing message file {0}: {1}", filePath, ex.Message);
            }
        }

        return new DataPlaneOperationResult<List<QueueMessage>>(OperationResult.Success, messages, null, null);
    }

    public DataPlaneOperationResult<QueueMessage> SendMessage(SubscriptionIdentifier subscriptionIdentifier,
        ResourceGroupIdentifier resourceGroupIdentifier, string storageAccountName, string queueName,
        string messageContent, int visibilityTimeout = 0, int messageTtl = 604800)
    {
        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(SendMessage),
            "Executing {0}: {1} {2} visibilityTimeout={3} messageTtl={4}",
            nameof(SendMessage), storageAccountName, queueName, visibilityTimeout, messageTtl);

        if (!controlPlane.QueueExists(subscriptionIdentifier, resourceGroupIdentifier, storageAccountName, queueName))
        {
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.NotFound, null,
                "Queue not found.", "QueueNotFound");
        }

        // Validate message size
        if (!QueueMessageValidator.ValidateMessageSize(messageContent, out var sizeError))
        {
            logger.LogDebug(nameof(QueueServiceDataPlane), nameof(SendMessage),
                "Message size validation failed: {0}", sizeError);
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.Failed, null,
                sizeError, "MessageTooLarge");
        }

        // Validate visibility timeout
        if (!QueueMessageValidator.ValidateVisibilityTimeout(visibilityTimeout, out var visibilityError))
        {
            logger.LogDebug(nameof(QueueServiceDataPlane), nameof(SendMessage),
                "Visibility timeout validation failed: {0}", visibilityError);
            return new DataPlaneOperationResult<QueueMessage>(OperationResult.Failed, null,
                visibilityError, "InvalidVisibilityTimeout");
        }

        var messageDir = resourceProvider.GetMessagesDirectoryPath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName);
        Directory.CreateDirectory(messageDir);

        // Generate unique message ID (GUID)
        var messageId = Guid.NewGuid().ToString();
        var messagePath = resourceProvider.GetMessageFilePath(subscriptionIdentifier, resourceGroupIdentifier,
            storageAccountName, queueName, messageId);

        // Create new message
        var message = new QueueMessage(messageId, messageContent)
        {
            VisibilityTimeout = visibilityTimeout,
            TimeToLive = messageTtl
        };

        // A freshly-enqueued message becomes visible at EnqueuedTime + visibilityTimeout
        // (== EnqueuedTime for the default timeout of 0, i.e. immediately visible). Always
        // populate NextVisibleTime so the enqueue response carries <TimeNextVisible>, which
        // the Azure Storage SDK's message parser dereferences unconditionally.
        message.NextVisibleTime = message.EnqueuedTime!.Value.AddSeconds(visibilityTimeout);

        // Calculate expiry time
        if (message.EnqueuedTime.HasValue && messageTtl > 0)
        {
            message.ExpiryTime = message.EnqueuedTime.Value.AddSeconds(messageTtl);
        }

        // Persist message atomically (temp file + rename) so a concurrent dequeue scan never reads a
        // half-written file.
        WriteMessageFileAtomic(messagePath, JsonSerializer.Serialize(message, GlobalSettings.JsonOptions));

        logger.LogDebug(nameof(QueueServiceDataPlane), nameof(SendMessage),
            "Message {0} enqueued to queue {1}", messageId, queueName);

        return new DataPlaneOperationResult<QueueMessage>(OperationResult.Success, message, null, null);
    }

    // Per-queue lock. The data plane is file-backed and instantiated per request, so a STATIC, queue-path-keyed
    // lock serialises concurrent dequeues of a given queue across all requests in this (single) process. Held
    // across the GetMessages read-check-modify-write scan, it makes the visibility update atomic so the
    // at-least-once queue hands a given message to exactly one worker instead of every concurrent poller.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> QueueLocks =
        new(StringComparer.Ordinal);

    private static object QueueLock(string messageDir)
        => QueueLocks.GetOrAdd(Path.GetFullPath(messageDir), static _ => new object());

    // Atomically (re)write a message file so a concurrent reader/writer never observes a torn message - the
    // foundation fix for the dequeue-writeback vs watchdog-visibility-update race that corrupted job triggers.
    // Shared with the table store via AtomicFile (the temp name is not "*.json", so the message scan skips it).
    private static void WriteMessageFileAtomic(string messagePath, string json)
        => AtomicFile.WriteAllText(messagePath, json);
}

public sealed class QueueEnumerationResult
{
    public string? StorageAccountName { get; set; }
    public QueueProperties[]? Queues { get; set; }

    public QueueEnumerationResult(string storageAccountName, QueueProperties[] queues)
    {
        StorageAccountName = storageAccountName;
        Queues = queues;
    }
}
