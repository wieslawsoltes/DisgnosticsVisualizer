using System.Net.Sockets;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

public sealed record NativeRelayResult(TraceReadResult Capture, int Connections, int Reconnects);

/// <summary>
/// Reliable native-file sidecar. Each reconnect replays the append-only recording from its header with
/// identical session/batch/event identities. Delivery is at least once, not a per-frame acknowledgement.
/// Backpressure reaches only the sidecar; the native recorder retains its independent bounded buffers.
/// </summary>
public static class NativeCaptureRelay
{
    public static Task<TraceReadResult> RunAsync(Stream source, IFrameConnection destination,
        TimeSpan idleTimeout, CancellationToken cancellationToken = default) =>
        RunCoreAsync(source, destination, idleTimeout, null, cancellationToken);

    /// <summary>Owns and disposes each returned stream/connection. Never reattaches a profiler on reconnect.</summary>
    public static async Task<NativeRelayResult> RunWithReconnectAsync(Func<Stream> openSource,
        Func<CancellationToken, Task<IFrameConnection>> connect, TimeSpan idleTimeout, int maximumReconnects = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openSource); ArgumentNullException.ThrowIfNull(connect);
        if (maximumReconnects is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(maximumReconnects));
        if (idleTimeout < TimeSpan.FromMilliseconds(100) || idleTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        (Guid Session, long Frequency, long Origin, long Utc)? identity = null;
        var connections = 0;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var destination = await connect(cancellationToken).ConfigureAwait(false);
                connections++;
                await using var source = openSource();
                if (source.CanSeek && source.Position != 0) throw new ArgumentException("Replay requires a stream positioned at its header.");
                var result = await RunCoreAsync(source, destination, idleTimeout, block =>
                {
                    var current = (block.SessionId, block.Frequency, block.OriginTimestamp, block.UtcStartTicks);
                    identity ??= current;
                    if (identity.Value != current) throw new InvalidDataException("Native source identity changed during reconnect.");
                }, cancellationToken).ConfigureAwait(false);
                return new(result, connections, attempt);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && attempt < maximumReconnects && e is IOException or SocketException)
            {
                // Integrity/authentication errors, explicit cancellation and source stalls are not retried.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 100 << attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
    private static async Task<TraceReadResult> RunCoreAsync(Stream source, IFrameConnection destination,
        TimeSpan idleTimeout, Action<TraceBlock>? inspect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var replies = ReplyAsync(destination, stop.Token);
        var read = NativeTraceFile.FollowAsync(source, block =>
        {
            inspect?.Invoke(block);
            return destination.SendAsync(TraceCodec.Encode(block), stop.Token);
        }, idleTimeout, stop.Token);
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
            // Observe both operations without masking the original failure.
            try { await read.ConfigureAwait(false); } catch { }
            try { await replies.ConfigureAwait(false); } catch { }
        }
    }
    private static async Task ReplyAsync(IFrameConnection connection, CancellationToken cancellationToken)
    {
        while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var command = CommandCodec.Decode(frame);
            if (command.Kind == CommandKind.Response) continue; // Session acknowledgement, not target control.
            var text = command.Kind == CommandKind.Ping ? "PONG" : command.Kind == CommandKind.RequestSnapshot
                ? "UNSUPPORTED_NATIVE_SNAPSHOT" : "UNSUPPORTED_NATIVE_CONTROL";
            await connection.SendAsync(CommandCodec.Encode(new(command.RequestId, CommandKind.Response, 0, text)), cancellationToken).ConfigureAwait(false);
        }
    }
}
