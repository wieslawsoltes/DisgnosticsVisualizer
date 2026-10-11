using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class SnapshotDeltaTests
{
    private static TraceBlock Block(Guid session, long sequence, int count = 256) => new()
    {
        SessionId = session, Sequence = sequence, Frequency = 1000,
        Symbols = [new(1, "Layout")], Objects = [new(1, "Window", "Root")],
        Events = [new(sequence, 0, sequence * 10, 5, 1, 0, 1, 1, Subsystem.Layout)],
        Snapshot = new(Enumerable.Range(1, count).Select(i => new NodeSnapshot(i, i == 1 ? 0 : 1, i == 1 ? 0 : 1, 0,
            "Avalonia.Controls.Button", "Button" + i, i * 10, 20, 80, 30, true, "primary")).ToArray(),
            [new(1, "Background", "Styled", "Redacted", "Style", "Button.primary")], [new(1, 256, "Template", "Button")], 1)
    };
    private static void Equal(TraceBlock expected, TraceBlock actual)
    {
        Assert.Equal(expected.SessionId, actual.SessionId); Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Events, actual.Events); Assert.Equal(expected.Symbols, actual.Symbols); Assert.Equal(expected.Objects, actual.Objects);
        Assert.Equal(expected.Snapshot!.Nodes, actual.Snapshot!.Nodes); Assert.Equal(expected.Snapshot.Properties, actual.Snapshot.Properties);
        Assert.Equal(expected.Snapshot.Relations, actual.Snapshot.Relations); Assert.Equal(expected.Snapshot.SelectedObject, actual.Snapshot.SelectedObject);
        Assert.Equal(expected.Snapshot.Truncated, actual.Snapshot.Truncated);
    }
    [Fact]
    public void FieldDeltasPreserveBothTreesOrderingPropertiesAndRelations()
    {
        var a = Block(Guid.NewGuid(), 1); var nodes = a.Snapshot!.Nodes.Skip(1).Reverse().ToList();
        nodes[0] = nodes[0] with { VisualParent = 2, LogicalParent = 4, TemplatedParent = 5, Width = 123, Name = "Changed", Visible = false, Classes = "active", X = -12.5 };
        nodes.Add(new(900, 2, 4, 5, "NewControl", "Inserted", 1, 2, 3, 4, true, ""));
        var b = a with { Sequence = 2, Snapshot = a.Snapshot with { Nodes = nodes.ToArray(), SelectedObject = 900, Truncated = true,
            Properties = [new(900, "X", "Direct", "1", "Local", "Layout")], Relations = [] } };
        var encoder = new TraceStreamEncoder(); var decoder = new TraceStreamDecoder();
        Equal(a, decoder.Decode(encoder.Encode(a)));
        var frame = encoder.Encode(b); Assert.Equal(FrameKind.SnapshotDelta, WireEnvelope.GetKind(frame));
        Assert.True(frame.Length < TraceCodec.Encode(b).Length / 3); Equal(b, decoder.Decode(frame));
        Assert.Equal(1, decoder.DeltaFrames); Assert.True(encoder.SavedBytes > 1000);
    }
    [Fact]
    public void RandomMutationRoundTripsAndPeriodicallyReanchors()
    {
        var encoder = new TraceStreamEncoder(8); var decoder = new TraceStreamDecoder(); var random = new Random(6821);
        var block = Block(Guid.NewGuid(), 1); var full = 0;
        for (var i = 0; i < 100; i++)
        {
            var nodes = block.Snapshot!.Nodes.ToArray();
            for (var change = 0; change < 4; change++) { var n = random.Next(nodes.Length); nodes[n] = nodes[n] with { Width = random.Next(250), Classes = i % 3 == 0 ? "active" : "" }; }
            if (i % 7 == 0) Array.Reverse(nodes);
            block = block with { Sequence = i + 1, Snapshot = block.Snapshot with { Nodes = nodes, SelectedObject = i % nodes.Length + 1, Truncated = i % 2 == 0 } };
            var frame = encoder.Encode(block); if (WireEnvelope.GetKind(frame) == FrameKind.Trace) full++;
            Equal(block, decoder.Decode(frame));
        }
        Assert.InRange(full, 12, 20); Assert.True(encoder.DeltaFrames > 70);
    }
    [Fact]
    public void MissingBaselineAndForeignSessionFailWithoutAdvancingState()
    {
        var a = Block(Guid.NewGuid(), 1); var b = a with { Sequence = 2 }; var c = b with { Sequence = 3 };
        var encoder = new TraceStreamEncoder(); var first = encoder.Encode(a); var second = encoder.Encode(b); var third = encoder.Encode(c);
        var decoder = new TraceStreamDecoder();
        Assert.Throws<InvalidDataException>(() => decoder.Decode(second)); decoder.Decode(first);
        Assert.Throws<InvalidDataException>(() => decoder.Decode(third)); Equal(b, decoder.Decode(second)); Equal(c, decoder.Decode(third));
        decoder.Decode(TraceCodec.Encode(a with { SessionId = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(second));
        decoder.Reset(); Equal(c, decoder.Decode(TraceCodec.Encode(c)));
    }
    [Fact]
    public void ChecksumTamperingAndEveryTruncationAreRejectedAtomically()
    {
        var a = Block(Guid.NewGuid(), 1); var encoder = new TraceStreamEncoder(); var first = encoder.Encode(a);
        var b = a with { Sequence = 2 }; var second = encoder.Encode(b); var decoder = new TraceStreamDecoder(); decoder.Decode(first);
        for (var length = 0; length < second.Length; length++)
            Assert.Throws<InvalidDataException>(() => decoder.Decode(second.AsSpan(0, length)));
        var corrupted = second.ToArray(); corrupted[70] ^= 1; // Target snapshot digest; then recompute envelope CRC.
        BinaryPrimitives.WriteUInt32LittleEndian(corrupted.AsSpan(corrupted.Length - 4), Crc32.Compute(corrupted.AsSpan(0, corrupted.Length - 4)));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(corrupted)); Equal(b, decoder.Decode(second));
    }
    [Fact]
    public void CallerMutationCannotChangeAnEncoderOrDecoderBaseline()
    {
        var a = Block(Guid.NewGuid(), 1); var encoder = new TraceStreamEncoder(); var decoder = new TraceStreamDecoder();
        var first = encoder.Encode(a); var observed = decoder.Decode(first);
        a.Snapshot!.Nodes[0] = a.Snapshot.Nodes[0] with { Name = "Caller changed its array" };
        observed.Snapshot!.Nodes[1] = observed.Snapshot.Nodes[1] with { Name = "Viewer changed its array" };
        var b = a with { Sequence = 2 }; Equal(b, decoder.Decode(encoder.Encode(b)));
    }
    [Fact]
    public void NegativeZeroGeometryBitsRemainConsistentWithContentHashes()
    {
        var a = Block(Guid.NewGuid(), 1); a.Snapshot!.Nodes[0] = a.Snapshot.Nodes[0] with { X = 0 };
        var nodes = a.Snapshot.Nodes.ToArray(); nodes[0] = nodes[0] with { X = -0d };
        var b = a with { Sequence = 2, Snapshot = a.Snapshot with { Nodes = nodes } };
        var encoder = new TraceStreamEncoder(); var decoder = new TraceStreamDecoder(); decoder.Decode(encoder.Encode(a));
        var actual = decoder.Decode(encoder.Encode(b)); Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(actual.Snapshot!.Nodes[0].X));
    }
    [Fact]
    public void SessionClockChangesAndReconnectsStartWithFullSnapshots()
    {
        var a = Block(Guid.NewGuid(), 1); var encoder = new TraceStreamEncoder(); encoder.Encode(a);
        Assert.Equal(FrameKind.Trace, WireEnvelope.GetKind(encoder.Encode(a with { Sequence = 2, Frequency = 2000 })));
        Assert.Equal(FrameKind.Trace, WireEnvelope.GetKind(encoder.Encode(a with { Sequence = 3, SessionId = Guid.NewGuid() })));
        encoder.Reset(); Assert.Equal(FrameKind.Trace, WireEnvelope.GetKind(encoder.Encode(a)));
    }
    [Fact]
    public async Task DeltaFilesRecoverOnlyAnIncompleteTail()
    {
        var a = Block(Guid.NewGuid(), 1); var encoder = new TraceStreamEncoder(); using var file = new MemoryStream();
        file.Write(encoder.Encode(a)); file.Write(encoder.Encode(a with { Sequence = 2 })); var incomplete = encoder.Encode(a with { Sequence = 3 });
        file.Write(incomplete.AsSpan(0, incomplete.Length - 1)); file.Position = 0;
        var blocks = new List<TraceBlock>(); var read = await TraceFile.ReadAsync(file, b => { blocks.Add(b); return ValueTask.CompletedTask; });
        Assert.True(read.TruncatedTail); Assert.Equal(2, read.Blocks); Assert.Equal(1, read.DeltaFrames); Equal(a with { Sequence = 2 }, blocks[^1]);
    }
    [Fact]
    public void InvalidNodeIdentityAndGeometryAreRejectedInFullFrames()
    {
        var a = Block(Guid.NewGuid(), 1);
        Assert.Throws<InvalidDataException>(() => TraceCodec.Encode(a with { Snapshot = a.Snapshot! with { Nodes = [a.Snapshot!.Nodes[0], a.Snapshot.Nodes[0]] } }));
        Assert.Throws<InvalidDataException>(() => TraceCodec.Encode(a with { Snapshot = a.Snapshot! with { Nodes = [a.Snapshot!.Nodes[0] with { Width = double.NaN }] } }));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticationNegotiatesWithoutBreakingLegacyClients(bool modern)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var token = NetworkSecurity.CreateToken();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var connection = new StreamFrameConnection(client.GetStream(), client);
                Assert.True(await ConnectionAuthentication.AuthenticateServerAsync(connection, token, true, timeout.Token));
                Assert.Equal(modern ? ProtocolFeatures.SnapshotDeltas : ProtocolFeatures.None, connection.Features);
            });
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            await using var connection = new StreamFrameConnection(client.GetStream());
            if (modern)
            {
                await ConnectionAuthentication.AuthenticateClientAsync(connection, token, true, timeout.Token);
                Assert.Equal(ProtocolFeatures.SnapshotDeltas, connection.Features);
            }
            else
            {
                await connection.SendAsync(CommandCodec.Encode(new(0, CommandKind.Authenticate, 1, token)), timeout.Token);
                var response = CommandCodec.Decode((await connection.ReceiveAsync(timeout.Token))!);
                Assert.Equal("AUTHENTICATED", response.Argument); Assert.Equal(0, response.ObjectId);
            }
            await server;
        }
        finally { listener.Stop(); }
    }
}
