using System.Net;
using System.Net.Sockets;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class UdpSenderTests
{
    [Fact]
    public async Task RealUdpSocketRoundtripUsesUniqueSequencesForRepeatedFrames()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var session = Guid.NewGuid(); var token = NetworkSecurity.CreateToken();
        using var assembler = new SecureDatagramAssembler(session, NetworkSecurity.DeriveDatagramKey(token));
        await using var sender = new UdpTelemetrySender(session, (IPEndPoint)listener.Client.LocalEndPoint!, token);
        var block = new TraceBlock
        {
            SessionId = session, Sequence = 0, Symbols = [new(1, "Measured")],
            Events = Enumerable.Range(1, 400).Select(i => new TraceEvent(i, 0, i * 100, 30, 1, 0, 1, 1, Subsystem.Layout)).ToArray()
        };
        var frame = TraceCodec.Encode(block);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            await sender.SendAsync(frame, timeout.Token);
            byte[]? completed = null;
            while (completed is null)
            {
                var packet = await listener.ReceiveAsync(timeout.Token);
                assembler.TryAccept(packet.Buffer, out completed);
            }
            Assert.Equal(frame, completed);
        }
        Assert.Equal(2, sender.SentFrames);
        Assert.True(sender.SentDatagrams > 2);
        Assert.Equal(0, assembler.DuplicatePackets);
        Assert.Equal(0, sender.Failures);
    }
    [Fact]
    public void AcknowledgementMustMatchTheAuthenticatedCaptureSession()
    {
        var session = Guid.NewGuid(); var command = DatagramNegotiation.Ready(session);
        Assert.True(DatagramNegotiation.IsReady(command, session));
        Assert.False(DatagramNegotiation.IsReady(command, Guid.NewGuid()));
        Assert.False(DatagramNegotiation.IsReady(command with { RequestId = 17 }, session));
        Assert.False(DatagramNegotiation.IsReady(command with { Kind = CommandKind.Ping }, session));
    }
    [Fact]
    public async Task QueueRetainsReliabilityWithoutReencodingThePayload()
    {
        var queue = new FrameQueue(2, 16); var data = new byte[8];
        Assert.True(queue.TryWrite(data, reliable: true)); queue.Complete();
        await foreach (var packet in queue.ReadPacketsAsync())
        { Assert.Same(data, packet.Bytes); Assert.True(packet.Reliable); }
    }
}
