using System.Buffers;
using System.Net.WebSockets;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

public sealed class WebSocketFrameConnection(WebSocket socket) : IFrameConnection
{
    public ProtocolFeatures Features { get; set; }
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        WireEnvelope.Validate(frame.Span);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            using var message = new MemoryStream(); var fragments = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Binary || ++fragments > 4096)
                    throw new InvalidDataException("Only bounded binary trace messages are accepted.");
                if (message.Length + result.Count > TraceLimits.MaxFrameBytes) throw new InvalidDataException("WebSocket frame exceeds limit.");
                message.Write(rented, 0, result.Count);
                if (message.Length >= WireEnvelope.HeaderBytes && message.Length > WireEnvelope.GetFrameLength(message.GetBuffer().AsSpan(0, WireEnvelope.HeaderBytes)))
                    throw new InvalidDataException("WebSocket message exceeds its declared trace length.");
                if (!result.EndOfMessage) continue;
                var bytes = message.ToArray(); WireEnvelope.Validate(bytes); return bytes;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { socket.Abort(); socket.Dispose(); }
        return ValueTask.CompletedTask;
    }
    public static async Task<WebSocketFrameConnection> ConnectAsync(Uri endpoint, string token,
        bool producer = false, CancellationToken cancellationToken = default)
    {
        NetworkSecurity.ValidateEndpoint(endpoint, "ws", "wss"); NetworkSecurity.ValidateToken(token);
        var socket = new ClientWebSocket();
        try
        {
            if (!OperatingSystem.IsBrowser()) socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var connection = new WebSocketFrameConnection(socket);
            await ConnectionAuthentication.AuthenticateClientAsync(connection, token, producer, timeout.Token).ConfigureAwait(false);
            return connection;
        }
        catch { socket.Dispose(); throw; }
    }
}
