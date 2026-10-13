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
        TimeSpan idleTimeout, CancellationToken cancellationToken = default, bool follow = true, INativeTargetController? targetControl = null) =>
        RunCoreAsync(source, destination, idleTimeout, null, cancellationToken, follow, targetControl);

    /// <summary>Owns and disposes each returned stream/connection. Never reattaches a profiler on reconnect.</summary>
    public static async Task<NativeRelayResult> RunWithReconnectAsync(Func<Stream> openSource,
        Func<CancellationToken, Task<IFrameConnection>> connect, TimeSpan idleTimeout, int maximumReconnects = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openSource); ArgumentNullException.ThrowIfNull(connect);
        if (maximumReconnects is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(maximumReconnects));
        if (idleTimeout < TimeSpan.FromMilliseconds(100) || idleTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        (Guid Session, long Frequency, long Origin, long Utc)? identity = null;
        var connections = 0; var digests = new List<byte[]>();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var destination = await connect(cancellationToken).ConfigureAwait(false);
                connections++;
                await using var verified = new NativeRelayClient.ReplayGuard(destination, digests, 131072, _ => { });
                await using var source = openSource();
                if (source.CanSeek && source.Position != 0) throw new ArgumentException("Replay requires a stream positioned at its header.");
                var result = await RunCoreAsync(source, verified, idleTimeout, block =>
                {
                    var current = (block.SessionId, block.Frequency, block.OriginTimestamp, block.UtcStartTicks);
                    identity ??= current;
                    if (identity.Value != current) throw new InvalidDataException("Native source identity changed during reconnect.");
                }, cancellationToken).ConfigureAwait(false);
                if (verified.Frames < digests.Count) throw new InvalidDataException("Native replay ended before its previously observed prefix.");
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
        TimeSpan idleTimeout, Action<TraceBlock>? inspect, CancellationToken cancellationToken, bool follow = true, INativeTargetController? targetControl = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        if (targetControl is not null && !follow) throw new ArgumentException("Control requires live native capture following.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = ReplyAsync(destination, session.Task, targetControl, stop.Token);
        ValueTask Send(TraceBlock block)
        { session.TrySetResult(block.SessionId); inspect?.Invoke(block); return destination.SendAsync(TraceCodec.Encode(block), stop.Token); }
        var read = follow ? NativeTraceFile.FollowAsync(source, Send, idleTimeout, stop.Token)
            : NativeTraceFile.ReadAsync(source, Send, false, stop.Token);
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
    private static async Task ReplyAsync(IFrameConnection connection, Task<Guid> session, INativeTargetController? targetControl, CancellationToken cancellationToken)
    {
        while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var command = CommandCodec.Decode(frame);
            if (command.Kind == CommandKind.Response) continue; // Session acknowledgement, not target control.
            var text = command.Kind == CommandKind.Capabilities ? (targetControl is null ? "NATIVE_READ_ONLY" :
                await targetControl.ExecuteAsync(await session.WaitAsync(cancellationToken).ConfigureAwait(false), command, cancellationToken).ConfigureAwait(false))
                : command.Kind == CommandKind.Ping ? "PONG" : command.Kind == CommandKind.RequestSnapshot
                ? "UNSUPPORTED_NATIVE_SNAPSHOT" : targetControl is null ? "UNSUPPORTED_NATIVE_CONTROL"
                : await targetControl.ExecuteAsync(await session.WaitAsync(cancellationToken).ConfigureAwait(false), command, cancellationToken).ConfigureAwait(false);
            await connection.SendAsync(CommandCodec.Encode(new(command.RequestId, CommandKind.Response, 0, text)), cancellationToken).ConfigureAwait(false);
        }
    }
}
