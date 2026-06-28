using System.Text;

namespace Topaz.Service.Storage.Utils;

internal static class QueueMessageValidator
{
    // Azure Queue Storage hard limit: 64 KB
    private const int MaxMessageSizeBytes = 65536;

    // Visibility timeout: 0-7 days (in seconds)
    private const int MinVisibilityTimeout = 0;
    private const int MaxVisibilityTimeout = 604800; // 7 days

    // Get Messages (dequeue) requires a STRICTLY positive visibility timeout: Azure Storage Queue's
    // Get Messages permits 1 second to 7 days (default 30s), unlike Put/Update Message which allow 0.
    // Enforcing this floor makes Topaz reject the invalid lease=0 dequeue that real Azure rejects, instead
    // of silently accepting it (which let the consuming emulator run on an invalid, dup-producing config).
    private const int MinGetMessagesVisibilityTimeout = 1;

    /// <summary>
    /// Validate message content size. Azure Queue Storage has a 64 KB limit for messages.
    /// Messages are base64-encoded, which adds ~33% overhead.
    /// </summary>
    /// <param name="content">Base64-encoded message content</param>
    /// <param name="errorMessage">Error description if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool ValidateMessageSize(string? content, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrEmpty(content))
        {
            return true; // Empty messages are allowed
        }

        var contentBytes = Encoding.UTF8.GetByteCount(content);

        // Check encoded size against 64 KB limit
        // Encoding validation is deferred to the endpoint
        if (contentBytes <= MaxMessageSizeBytes) return true;
        errorMessage = $"Message size exceeds maximum allowed size of {MaxMessageSizeBytes} bytes. " +
                       $"Current size: {contentBytes} bytes.";
        return false;
    }

    /// <summary>
    /// Validate visibility timeout value.
    /// </summary>
    /// <param name="visibilityTimeout">Timeout in seconds</param>
    /// <param name="errorMessage">Error description if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool ValidateVisibilityTimeout(int visibilityTimeout, out string? errorMessage)
    {
        errorMessage = null;

        if (visibilityTimeout >= MinVisibilityTimeout && visibilityTimeout <= MaxVisibilityTimeout) return true;
        errorMessage = $"Visibility timeout must be between {MinVisibilityTimeout} and {MaxVisibilityTimeout} seconds. " +
                       $"Current value: {visibilityTimeout} seconds.";
        return false;
    }

    /// <summary>
    /// Validate the visibility timeout for a Get Messages (dequeue) request. Azure Storage Queue's Get Messages
    /// requires the value to be 1 second to 7 days (default 30s) - it does NOT permit 0, unlike Put/Update Message.
    /// A request outside this range is rejected with 400 OutOfRangeQueryParameterValue.
    /// </summary>
    /// <param name="visibilityTimeout">Timeout in seconds.</param>
    /// <param name="errorMessage">Error description if validation fails.</param>
    /// <returns>True if valid, false otherwise.</returns>
    public static bool ValidateGetMessagesVisibilityTimeout(int visibilityTimeout, out string? errorMessage)
    {
        errorMessage = null;

        if (visibilityTimeout >= MinGetMessagesVisibilityTimeout && visibilityTimeout <= MaxVisibilityTimeout) return true;
        errorMessage = $"Visibility timeout for Get Messages must be between {MinGetMessagesVisibilityTimeout} and " +
                       $"{MaxVisibilityTimeout} seconds. Current value: {visibilityTimeout} seconds.";
        return false;
    }

    /// <summary>The minimum visibility timeout (seconds) Get Messages permits, for building Azure error responses.</summary>
    public static int GetMessagesMinimumVisibilityTimeout => MinGetMessagesVisibilityTimeout;

    /// <summary>The maximum visibility timeout (seconds) Get Messages permits, for building Azure error responses.</summary>
    public static int GetMessagesMaximumVisibilityTimeout => MaxVisibilityTimeout;

    /// <summary>
    /// Get the HTTP status code for message size violation.
    /// </summary>
    public static System.Net.HttpStatusCode GetPayloadTooLargeStatusCode() => (System.Net.HttpStatusCode)413;

    /// <summary>
    /// Get the error code name for message size violation.
    /// </summary>
    public static string GetPayloadTooLargeErrorCode() => "RequestBodyTooLarge";
}
