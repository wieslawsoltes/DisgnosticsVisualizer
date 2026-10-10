using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Collector;

public sealed class SocketListeners(CollectorOptions options, CaptureHub hub, ILogger<SocketListeners> logger) : BackgroundService
{
    private readonly SemaphoreSlim _connections = new(32, 32);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(options.TcpEndpoint.Host.Trim('[', ']')), options.TcpEndpoint.Port);
        listener.Start(32);
        var udp = options.UdpEndpoint is null ? Task.CompletedTask : ReceiveUdpAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                if (!await _connections.WaitAsync(0, stoppingToken).ConfigureAwait(false)) { client.Dispose(); continue; }
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { listener.Stop(); try { await udp.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    }
    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            client.NoDelay = true; client.ReceiveBufferSize = 256 * 1024; client.SendBufferSize = 256 * 1024;
            Stream stream = client.GetStream();
            if (options.TcpEndpoint.Scheme == "tls")
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var ssl = new SslStream(stream, false);
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath!, options.CertificatePassword);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token).ConfigureAwait(false);
                stream = ssl;
            }
            await using var connection = new StreamFrameConnection(stream, client);
            var producer = await ConnectionAuthentication.AuthenticateServerAsync(connection, options.Token, null, stoppingToken).ConfigureAwait(false);
            if (producer) await hub.RunProducerAsync(connection, stoppingToken).ConfigureAwait(false);
            else await hub.RunViewerAsync(connection, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { logger.LogDebug("Diagnostics connection ended: {FailureType}", error.GetType().Name); }
        finally { client.Dispose(); _connections.Release(); }
    }
    private async Task ReceiveUdpAsync(CancellationToken stoppingToken)
    {
        using var socket = new UdpClient(options.UdpEndpoint!); socket.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        while (!stoppingToken.IsCancellationRequested)
        {
            var datagram = await socket.ReceiveAsync(stoppingToken).ConfigureAwait(false);
            if (datagram.Buffer.Length > DatagramLimits.Mtu) continue;
            try { await hub.AcceptDatagramAsync(datagram.Buffer, stoppingToken).ConfigureAwait(false); }
            catch (InvalidDataException) { logger.LogDebug("Rejected malformed authenticated trace batch."); }
        }
    }
}
