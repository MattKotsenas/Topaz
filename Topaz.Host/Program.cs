using Spectre.Console.Cli;

namespace Topaz.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Pre-grow the thread pool. Each new TLS connection runs a CPU-bound key-exchange
        // handshake whose write/read continuations are scheduled on pool threads; the runtime
        // only adds worker threads ~1-2/sec, so a burst of concurrent clients (e.g. a client
        // polling many queues at once) can stall mid-handshake waiting for a thread, leaving
        // connections hung well past the client's request timeout. A higher floor lets those
        // handshakes complete promptly under burst.
        ThreadPool.GetMinThreads(out var minWorker, out var minCompletionPort);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 256), Math.Max(minCompletionPort, 256));

        var app = new CommandApp<StartHostCommand>();
        app.Configure(config => config.SetApplicationName("topaz-host"));
        return await app.RunAsync(args);
    }
}
