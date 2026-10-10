using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class TransportTests
{
    private static TraceBlock Block(Guid session) => new()
    {
        SessionId = session, Sequence = 1,
        Symbols = [new(1, "Sample.Layout.Measure")],
        Events = Enumerable.Range(1, 500).Select(i => new TraceEvent(i, i - 1, i * 12345, 4567,
            1, 0, 1, 1, Subsystem.Layout)).ToArray()
    };
    [Fact] public void DatagramReassemblySupportsReorderingAndRejectsReplay()
    {
        var key = RandomNumberGenerator.GetBytes(32); var session = Guid.NewGuid(); var frame = TraceCodec.Encode(Block(session));
        using var sender = new SecureDatagramEncoder(session, key); using var receiver = new SecureDatagramAssembler(session, key);
        var packets = sender.Encode(1, frame); Assert.True(packets.Count > 1); Assert.All(packets, p => Assert.True(p.Length <= 1200));
        byte[]? result = null;
        foreach (var packet in packets.Reverse()) if (receiver.TryAccept(packet, out var complete)) result = complete;
        Assert.Equal(frame, result);
        foreach (var packet in packets) Assert.False(receiver.TryAccept(packet, out _));
        Assert.True(receiver.DuplicatePackets > 0);
        Assert.Throws<InvalidOperationException>(() => sender.Encode(1, frame));
    }
    [Fact] public void DatagramTamperingAndForeignSessionsAreRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32); var session = Guid.NewGuid();
        using var sender = new SecureDatagramEncoder(session, key); using var receiver = new SecureDatagramAssembler(session, key);
        var packet = sender.Encode(1, TraceCodec.Encode(Block(session)))[0].ToArray(); packet[^1] ^= 1;
        Assert.False(receiver.TryAccept(packet, out _)); Assert.Equal(1, receiver.RejectedPackets);
        using var foreign = new SecureDatagramAssembler(Guid.NewGuid(), key);
        Assert.False(foreign.TryAccept(packet, out _));
    }
    [Fact] public void InsecureRemoteEndpointsAndEmbeddedSecretsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => NetworkSecurity.ValidateEndpoint(new("tcp://example.com:4318"), "tcp", "tls"));
        Assert.Throws<ArgumentException>(() => NetworkSecurity.ValidateEndpoint(new("wss://example.com/trace?token=secret"), "ws", "wss"));
        NetworkSecurity.ValidateEndpoint(new("tcp://127.0.0.1:4318"), "tcp");
        NetworkSecurity.ValidateEndpoint(new("wss://example.com/trace"), "ws", "wss");
    }
    [Fact] public async Task AuthenticatedTcpLoopbackRoundtrip()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var token = NetworkSecurity.CreateToken(); var expected = TraceCodec.Encode(Block(Guid.NewGuid()));
            var server = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var connection = new StreamFrameConnection(client.GetStream(), client);
                Assert.True(await ConnectionAuthentication.AuthenticateServerAsync(connection, token, true, timeout.Token));
                Assert.Equal(expected, await connection.ReceiveAsync(timeout.Token));
            }, timeout.Token);
            await using var producer = await StreamFrameConnection.ConnectAsync(new($"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), token, true, timeout.Token);
            await producer.SendAsync(expected, timeout.Token); await server;
        }
        finally { listener.Stop(); }
    }
    [Fact] public async Task BoundedQueueRefusesOverflow()
    {
        var queue = new FrameQueue(2, 10); Assert.True(queue.TryWrite(new byte[6])); Assert.False(queue.TryWrite(new byte[6]));
        queue.Complete(); var count = 0; await foreach (var frame in queue.ReadAllAsync()) { Assert.Equal(6, frame.Length); count++; }
        Assert.Equal(1, count);
    }
}
