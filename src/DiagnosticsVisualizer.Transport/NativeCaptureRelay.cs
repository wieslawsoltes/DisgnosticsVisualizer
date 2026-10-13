using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

/// <summary>
/// Relays a validated growing native recording over an already authenticated reliable connection.
/// Backpressure affects the sidecar, never the target's hot callback. No unbounded frame queue is used.
/// Capture control is intentionally separate: this class neither attaches nor rewrites a process.
/// </summary>
public static class NativeCaptureRelay
{
    public static async Task<TraceReadResult> RunAsync(Stream source, IFrameConnection destination,
        TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var replies = ReplyAsync(destination, stop.Token);
        var read = NativeTraceFile.FollowAsync(source,
            block => destination.SendAsync(TraceCodec.Encode(block), stop.Token), idleTimeout, stop.Token);
        try
        {
            var completed = await Task.WhenAny(read, replies).ConfigureAwait(false);
            if (completed == replies)
            {
                await replies.ConfigureAwait(false);
                throw new IOException("Collector disconnected before native capture completed.");
            }
            return await read.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            // Observe both operations, while retaining the original failure from the try block.
            try { await read.ConfigureAwait(false); } catch { }
            try { await replies.ConfigureAwait(false); } catch { }
        }
    }
    private static async Task ReplyAsync(IFrameConnection connection, CancellationToken cancellationToken)
    {
        while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var command = CommandCodec.Decode(frame);
            if (command.Kind == CommandKind.Response) continue; // Authenticated collector session acknowledgement.
            var text = command.Kind == CommandKind.Ping ? "PONG" : command.Kind == CommandKind.RequestSnapshot
                ? "UNSUPPORTED_NATIVE_SNAPSHOT" : "UNSUPPORTED_NATIVE_CONTROL";
            await connection.SendAsync(CommandCodec.Encode(new(command.RequestId, CommandKind.Response, 0, text)), cancellationToken).ConfigureAwait(false);
        }
    }
}
