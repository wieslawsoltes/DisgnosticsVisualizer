using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

/// <summary>
/// A capture-session-owned UDP sender. Keep this instance across reliable-channel reconnects:
/// its monotonically increasing packet counter must never restart with the same session key.
/// Checkpoints and control messages belong on the authenticated reliable channel.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class UdpTelemetrySender : IAsyncDisposable
{
    private readonly UdpClient _socket;
    private readonly SecureDatagramEncoder _encoder;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private long _sequence = -1, _frames, _datagrams, _failures;
    private int _disposed;
    public Guid SessionId { get; }
    public long SentFrames => Interlocked.Read(ref _frames);
    public long SentDatagrams => Interlocked.Read(ref _datagrams);
    public long Failures => Interlocked.Read(ref _failures);
    public UdpTelemetrySender(Guid session, IPEndPoint endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint));
        NetworkSecurity.ValidateToken(token);
        SessionId = session;
        var key = NetworkSecurity.DeriveDatagramKey(token);
        try { _encoder = new(session, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
        _socket = new(endpoint.AddressFamily);
        try { _socket.Connect(endpoint); }
        catch { _socket.Dispose(); _encoder.Dispose(); throw; }
    }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (frame.Length > DatagramLimits.MaxFrame || WireEnvelope.GetKind(frame.Span) != FrameKind.Trace ||
            frame.Length < WireEnvelope.HeaderBytes + 16 + 4 || new Guid(frame.Span.Slice(WireEnvelope.HeaderBytes, 16)) != SessionId)
            throw new InvalidDataException("UDP telemetry must be a bounded trace frame belonging to this capture session.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_sequence == long.MaxValue) throw new InvalidOperationException("Start a new capture session before exhausting the nonce space.");
            var packets = _encoder.Encode(++_sequence, frame.Span);
            foreach (var packet in packets)
            {
                await _socket.SendAsync(packet.AsMemory(), linked.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _datagrams);
            }
            Interlocked.Increment(ref _frames);
        }
        catch (SocketException) { Interlocked.Increment(ref _failures); throw; }
        finally { _send.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _send.WaitAsync().ConfigureAwait(false);
        try { _socket.Dispose(); _encoder.Dispose(); }
        finally { _send.Release(); _stop.Dispose(); }
    }
}

/// <summary>Reliable-channel acknowledgement that the collector has installed the source-session UDP key.</summary>
public static class DatagramNegotiation
{
    private const string Prefix = "UDP_READY:";
    public static TraceCommand Ready(Guid session) => new(0, CommandKind.Response, 0, Prefix + session.ToString("N"));
    public static bool IsReady(TraceCommand command, Guid session) =>
        command.Kind == CommandKind.Response && command.RequestId == 0 &&
        command.Argument.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(command.Argument.AsSpan(Prefix.Length), "N", out var acknowledged) &&
        acknowledged != Guid.Empty && acknowledged == session;
}
