using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Topaz.Host.Diagnostics;

/// <summary>
/// Request tracing for Topaz, emitting OpenTelemetry-shaped spans (one JSON object per request, with
/// semantic-convention attributes) to a dedicated trace file.
///
/// Why not a global <see cref="ActivityListener"/> (the usual OpenTelemetry .NET path): Topaz hosts requests
/// on ASP.NET Core 2.3 running on the .NET 10 runtime. Subscribing a process-global ActivityListener perturbs
/// that old hosting layer's per-request diagnostics and hangs request handling (verified: with a listener
/// subscribed, Kestrel accepts connections but never processes them). So we instrument the Router directly
/// (<see cref="TryRecordRequest"/>) and write spans ourselves. The data shape is identical to an OTel span,
/// and an <see cref="ActivitySource"/> is still defined, so once Topaz moves to modern hosting this can switch
/// to ActivitySource + the OTel SDK (OTLP / Jaeger / Aspire) with no change to the instrumentation points.
///
/// OFF by default: with <c>TOPAZ_OTEL_TRACES</c> unset, <see cref="TryRecordRequest"/> is a cheap no-op.
/// </summary>
// TODO(upstream/observability): move Topaz.Host off the legacy `Microsoft.AspNetCore` 2.3.9 NuGet
// metapackage (the last standalone ASP.NET Core package; 3.0+ ships it as the `Microsoft.AspNetCore.App`
// shared framework via <FrameworkReference>) onto the current ASP.NET Core that matches the net10 runtime.
// That would (a) remove the Kestrel 2.3.0 CVE (NU1904) and (b) make almost all of THIS file unnecessary:
// modern ASP.NET Core auto-emits a per-request Activity, so `AddOpenTelemetry().WithTracing(t => t
// .AddAspNetCoreInstrumentation().AddSource("Topaz").AddOtlpExporter())` yields request spans for free and
// the existing ActivitySource "Topaz" lights up unchanged - no Router instrumentation, no file drain, no
// manual span JSON. The only reason for the bespoke approach is that subscribing any ActivityListener hangs
// the 2.3 hosting layer (see above); modernizing removes that blocker. Scoped here as a tracked follow-up.
internal static class TopazDiagnostics
{
    /// <summary>
    /// Defined for forward-compatibility: when Topaz moves off ASP.NET Core 2.3, replace the direct
    /// <see cref="TryRecordRequest"/> calls with <c>ActivitySource.StartActivity</c> and wire the OTel SDK
    /// (<c>AddSource("Topaz")</c>) to export to OTLP/Jaeger/Aspire unchanged.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Topaz", "0.1.0");

    private static SpanFileWriter? s_active;
    private static int s_recordFailureReported;

    /// <summary>True when request tracing is active (a trace file is open).</summary>
    public static bool IsEnabled => s_active is not null;

    /// <summary>
    /// When <c>TOPAZ_OTEL_TRACES</c> names a writable file path, opens the trace file and returns the writer
    /// (dispose on shutdown). Returns null when tracing is disabled or the path is not writable.
    /// </summary>
    public static IDisposable? TryStart()
    {
        var target = Environment.GetEnvironmentVariable("TOPAZ_OTEL_TRACES");
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        try
        {
            var writer = new SpanFileWriter(target);
            s_active = writer;
            Console.Error.WriteLine("[topaz-otel] request tracing enabled; spans -> " + target);
            return writer;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[topaz-otel] could not open trace file '" + target + "': " + ex.Message +
                " - request tracing disabled.");
            return null;
        }
    }

    /// <summary>
    /// Records one completed request as an OpenTelemetry-shaped span. Called directly from the Router on the
    /// request thread; cheap (format + non-blocking enqueue) and a no-op when tracing is disabled. The actual
    /// file write happens on a dedicated background thread (see <see cref="SpanFileWriter"/>).
    /// </summary>
    public static void TryRecordRequest(
        DateTime startUtc,
        double durationMs,
        string method,
        string path,
        string? query,
        int port,
        string? endpointName,
        string? providerNamespace,
        int statusCode,
        Exception? exception,
        string? traceParent = null)
    {
        var writer = s_active;
        if (writer is null)
        {
            return;
        }

        try
        {
            // OpenTelemetry HTTP server semantic conventions, plus topaz.* for emulator-specific context.
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["t"] = startUtc.ToString("O", CultureInfo.InvariantCulture),
                ["name"] = endpointName ?? "UnmatchedRequest",
                ["ms"] = Math.Round(durationMs, 3),
                ["status"] = statusCode >= 500 ? "Error" : "Ok",
                ["http.request.method"] = method,
                ["url.path"] = path,
                ["server.port"] = port,
                ["http.response.status_code"] = statusCode,
            };

            if (TryParseTraceParent(traceParent, out var traceId, out var parentSpanId))
            {
                record["traceId"] = traceId;
                record["spanId"] = CreateSpanId();
                record["parentSpanId"] = parentSpanId;
            }

            if (!string.IsNullOrEmpty(query))
            {
                record["url.query"] = query;
            }

            if (!string.IsNullOrEmpty(endpointName))
            {
                record["topaz.endpoint"] = endpointName;
            }

            if (!string.IsNullOrEmpty(providerNamespace))
            {
                record["topaz.provider_namespace"] = providerNamespace;
            }

            if (exception is not null)
            {
                record["exception.type"] = exception.GetType().FullName;
                record["exception.message"] = exception.Message;
            }

            writer.Enqueue(JsonSerializer.Serialize(record));
        }
        catch (Exception ex)
        {
            // Telemetry must never affect request processing, so the request thread always continues.
            // Report the first failure once (guarded) so a silently-broken tracer is still discoverable
            // without flooding the hot path.
            if (Interlocked.CompareExchange(ref s_recordFailureReported, 1, 0) == 0)
            {
                Console.Error.WriteLine("[topaz-otel] span recording failed (further failures suppressed): " + ex.Message);
            }
        }
    }

    private static bool TryParseTraceParent(string? traceParent, out string traceId, out string parentSpanId)
    {
        traceId = string.Empty;
        parentSpanId = string.Empty;
        if (string.IsNullOrWhiteSpace(traceParent))
        {
            return false;
        }

        var parts = traceParent.Split('-');
        if (parts.Length != 4
            || !string.Equals(parts[0], "00", StringComparison.OrdinalIgnoreCase)
            || !IsLowerHexLength(parts[1], 32)
            || !IsLowerHexLength(parts[2], 16)
            || IsAllZero(parts[1])
            || IsAllZero(parts[2]))
        {
            return false;
        }

        traceId = parts[1].ToLowerInvariant();
        parentSpanId = parts[2].ToLowerInvariant();
        return true;
    }

    private static string CreateSpanId()
    {
        Span<byte> bytes = stackalloc byte[8];
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (IsAllZero(bytes));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerHexLength(string value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZero(string value)
    {
        foreach (var c in value)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Owns the trace file and a dedicated background drain thread. Producers (request threads) enqueue
    /// formatted lines without blocking; the single drain thread writes them, so request handling is never
    /// blocked on trace I/O. The queue is bounded with drop-on-full as the backpressure safety valve.
    /// </summary>
    private sealed class SpanFileWriter : IDisposable
    {
        private const int QueueCapacity = 200_000;

        private readonly BlockingCollection<string> _queue = new(QueueCapacity);
        private readonly StreamWriter _writer;
        private readonly Thread _drainThread;
        private long _dropped;

        public SpanFileWriter(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };

            _drainThread = new Thread(DrainLoop)
            {
                IsBackground = true,
                Name = "topaz-otel-span-writer",
            };
            _drainThread.Start();
        }

        public void Enqueue(string line)
        {
            if (!_queue.TryAdd(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        private void DrainLoop()
        {
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    _writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[topaz-otel] span writer stopped: " + ex.Message);
            }
        }

        public void Dispose()
        {
            s_active = null;
            _queue.CompleteAdding();
            _drainThread.Join(TimeSpan.FromSeconds(5));
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("[topaz-otel] error flushing trace file on shutdown: " + ex.Message);
            }

            var dropped = Interlocked.Read(ref _dropped);
            if (dropped > 0)
            {
                Console.Error.WriteLine("[topaz-otel] dropped " + dropped +
                    " span(s) under load (queue full); request handling was never blocked.");
            }
        }
    }
}
