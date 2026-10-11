using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

public sealed class StreamFrameConnection(Stream stream, IDisposable? owner = null) : IFrameConnection
{
    public ProtocolFeatures Features { get; set; }
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        WireEnvelope.Validate(frame.Span);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }
    public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        => WireEnvelope.ReadAsync(stream, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await stream.DisposeAsync().ConfigureAwait(false); owner?.Dispose();
    }
    public static async Task<StreamFrameConnection> ConnectAsync(Uri endpoint, string token,
        bool producer = true, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Browsers connect through the WebSocket gateway.");
        NetworkSecurity.ValidateEndpoint(endpoint, "tcp", "tls"); NetworkSecurity.ValidateToken(token);
        var client = new TcpClient { NoDelay = true, SendBufferSize = 256 * 1024, ReceiveBufferSize = 256 * 1024 };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            // Do not resolve a plaintext localhost connection through potentially rebinding DNS.
            if (endpoint.Scheme == "tcp")
            {
                var ip = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(endpoint.Host.Trim('[', ']'));
                await client.ConnectAsync(ip, endpoint.Port, timeout.Token).ConfigureAwait(false);
            }
            else await client.ConnectAsync(endpoint.DnsSafeHost, endpoint.Port, timeout.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (endpoint.Scheme == "tls")
            {
                var tls = new SslStream(stream, false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.DnsSafeHost, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, timeout.Token).ConfigureAwait(false);
                stream = tls;
            }
            var connection = new StreamFrameConnection(stream, client);
            try { await ConnectionAuthentication.AuthenticateClientAsync(connection, token, producer, timeout.Token).ConfigureAwait(false); return connection; }
            catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch { client.Dispose(); throw; }
    }
}
