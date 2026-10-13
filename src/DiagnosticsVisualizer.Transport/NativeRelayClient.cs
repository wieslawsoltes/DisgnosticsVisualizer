using System.Security.Cryptography;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

public sealed record NativeRelayOptions
{
    public bool Follow { get; init; }
    public INativeTargetController? TargetControl { get; init; }
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaximumReplayFrames { get; init; } = 131072;
    internal void Validate()
    {
        if (TargetControl is not null && !Follow) throw new ArgumentException("Native target control requires explicit live following.");
        if (MaximumAttempts is < 1 or > 10 || MaximumReplayFrames is < 1 or > 1_000_000 ||
            IdleTimeout < TimeSpan.FromMilliseconds(100) || IdleTimeout > TimeSpan.FromHours(1) ||
            RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(NativeRelayOptions));
    }
}

public sealed record NativeRelayReport(TraceReadResult Capture, int Attempts, long FramesSent, int UniqueDecodedFrames, long ReplayedFrames);

/// <summary>
/// Reliable native sidecar with bounded reconnect replay. Connections returned by the factory must
/// already be authenticated. The same seekable handle is retained; replacement paths are not opened.
/// Replay preserves source sequences and rejects any changed previously decoded frame. Receivers
/// deduplicate the stable session/sequence identities. Writes are not application-level acknowledgements.
/// </summary>
public static class NativeRelayClient
{
    public static Task<NativeRelayReport> RelayAsync(Stream source, Uri endpoint, string token,
        NativeRelayOptions? options = null, CancellationToken cancellationToken = default)
    {
        NetworkSecurity.ValidateEndpoint(endpoint, "tcp", "tls"); NetworkSecurity.ValidateToken(token);
        if (endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath is not ("" or "/"))
            throw new ArgumentException("Specify a TCP/TLS host and explicit port, without a path.", nameof(endpoint));
        return RunAsync(source, async ct => await StreamFrameConnection.ConnectAsync(endpoint, token, true, ct).ConfigureAwait(false), options, cancellationToken);
    }

    public static async Task<NativeRelayReport> RunAsync(Stream source,
        Func<CancellationToken, Task<IFrameConnection>> connectAuthenticated,
        NativeRelayOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(connectAuthenticated);
        options ??= new(); options.Validate();
        if (!source.CanRead || !source.CanSeek || source.Position != 0)
            throw new ArgumentException("A readable, seekable native file positioned at zero is required.", nameof(source));
        var digests = new List<byte[]>(); long sent = 0, replayed = 0, minimumLength = 0;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length < minimumLength) throw new InvalidDataException("Native capture was truncated during relay.");
            source.Position = 0;
            try
            {
                await using var connection = await connectAuthenticated(cancellationToken).ConfigureAwait(false);
                await using var verified = new ReplayGuard(connection, digests, options.MaximumReplayFrames,
                    replay => { sent++; if (replay) replayed++; });
                var result = await NativeCaptureRelay.RunAsync(source, verified, options.IdleTimeout,
                    cancellationToken, options.Follow, options.TargetControl).ConfigureAwait(false);
                if (verified.Frames < digests.Count) throw new InvalidDataException("Replay ended before previously decoded frames.");
                return new(result, attempt, sent, digests.Count, replayed);
            }
            catch (Exception error) when (attempt < options.MaximumAttempts && !cancellationToken.IsCancellationRequested &&
                (error is IOException or System.Net.Sockets.SocketException))
            {
                minimumLength = Math.Max(minimumLength, source.Position);
                await Task.Delay(options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class ReplayGuard(IFrameConnection inner, List<byte[]> digests, int maximumFrames, Action<bool> sent) : IFrameConnection
    {
        public int Frames { get; private set; }
        public ProtocolFeatures Features { get => inner.Features; set => inner.Features = value; }
        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) => inner.ReceiveAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Outer scope owns the authenticated connection.
        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (WireEnvelope.GetKind(frame.Span) != FrameKind.Trace)
            { await inner.SendAsync(frame, cancellationToken).ConfigureAwait(false); return; }
            var digest = SHA256.HashData(frame.Span); var replay = Frames < digests.Count;
            if (replay)
            {
                if (!CryptographicOperations.FixedTimeEquals(digests[Frames], digest))
                    throw new InvalidDataException("Previously decoded native content changed during reconnect replay.");
            }
            else
            {
                if (digests.Count >= maximumFrames) throw new InvalidDataException("Native relay replay budget exceeded.");
                digests.Add(digest);
            }
            Frames++;
            await inner.SendAsync(frame, cancellationToken).ConfigureAwait(false); sent(replay);
        }
    }
}
