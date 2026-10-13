using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;

namespace DiagnosticsVisualizer.Tests;
public sealed class NativeControlTests
{
    private static readonly string Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    [Fact] public async Task SessionNonceOperationAndResultAreValidated()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var session = Guid.NewGuid();
        using var client = new NativeControlClient(((IPEndPoint)server.LocalEndpoint).Port, Secret);
        var exchange = Task.Run(async () => {
            using var peer = await server.AcceptTcpClientAsync(deadline.Token); var stream = peer.GetStream();
            var request = new byte[72]; await stream.ReadExactlyAsync(request, deadline.Token);
            Assert.Equal("DVNCMD01", Encoding.ASCII.GetString(request, 0, 8));
            Assert.Equal(Convert.FromHexString(Secret), request[24..56]);
            var reply = Reply(request, state: 1);
            // Every field is allowed to arrive in a separate physical read.
            foreach (var b in reply) await stream.WriteAsync(new byte[] { b }, deadline.Token);
        });
        var result = await client.SendAsync(session, NativeControlOperation.Pause, deadline.Token); await exchange;
        Assert.Equal(1, result.State); Assert.Equal(0, result.HResult);
    }
    [Theory] [InlineData(8)] [InlineData(24)] [InlineData(32)] [InlineData(40)] [InlineData(48)] [InlineData(52)]
    public async Task IncorrectResponseIdentityOrShapeCannotBecomeSuccess(int offset)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var client = new NativeControlClient(((IPEndPoint)server.LocalEndpoint).Port, Secret);
        var exchange = Task.Run(async () => {
            using var peer = await server.AcceptTcpClientAsync(deadline.Token); var request = new byte[72]; await peer.GetStream().ReadExactlyAsync(request, deadline.Token);
            var reply = Reply(request); reply[offset] = 255; await peer.GetStream().WriteAsync(reply, deadline.Token);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(Guid.NewGuid(), NativeControlOperation.Status, deadline.Token)); await exchange;
    }
    [Fact] public async Task TimeoutCancellationAndDisposalRemainDistinct()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var client = new NativeControlClient(((IPEndPoint)server.LocalEndpoint).Port, Secret, TimeSpan.FromMilliseconds(100));
        var send = client.SendAsync(Guid.NewGuid(), NativeControlOperation.Status);
        using var peer = await server.AcceptTcpClientAsync(deadline.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => send);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Guid.NewGuid(), NativeControlOperation.Status, stop.Token));
        client.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(Guid.NewGuid(), NativeControlOperation.Status));
    }
    [Fact] public void CapabilitiesArePrivateOptInAndLiveOnly()
    {
        Assert.Throws<ArgumentException>(() => new NativeControlClient(1234, "bad-token"));
        Assert.Throws<ArgumentException>(() => new NativeControlClient(1234, new string('0', 64)));
        using var client = new NativeControlClient(1234, Secret);
        Assert.DoesNotContain(Secret, client.ToString());
    }
    [Fact] public async Task V4ControlTransitionsAndRecordingStateSurviveExport()
    {
        var store = new TraceStore(); var blocks = new List<TraceBlock>();
        using var input = new MemoryStream(Recording());
        await NativeTraceFile.FollowAsync(input, b => { store.Add(b); blocks.Add(b); return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(1));
        var states = blocks.Where(b => b.Symbols.Any(s => NativeMetadataSymbol.TryDecode(s.Name, out var m) && m is NativeControlState)).ToArray();
        Assert.Equal(new[] { true, false, true }, states.Select(b => b.Recording));
        var index = new NativeCaptureIndex(store.GetView()); Assert.Equal(4, index.Process!.FormatVersion); Assert.Equal(3, index.Control!.Revision);
        Assert.False(index.Incomplete); Assert.Equal(1, index.PairedSpans);
        using var export = new MemoryStream(); await TraceExport.WriteBinaryAsync(export, store.GetView()); export.Position = 0;
        var copy = new TraceStore(); await TraceImport.ReadAsync(export, b => { copy.Add(b); return ValueTask.CompletedTask; }, false);
        Assert.Equal(index.Control, new NativeCaptureIndex(copy.GetView()).Control);
    }
    [Fact] public async Task EveryControlByteMutationAndTruncationIsRejected()
    {
        var bytes = Recording();
        for (var n = 68; n < 68 + 3 * 44; n++) {
            var copy = bytes.ToArray(); copy[n] ^= 0x40;
            using var input = new MemoryStream(copy);
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, false));
        }
        for (var n = 64; n < bytes.Length; n++) {
            using var input = new MemoryStream(bytes[..n]);
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, false));
        }
    }
    [Theory] [InlineData(1, 0, 0)] [InlineData(3, 1, 1)] [InlineData(2, 3, 1)] [InlineData(2, 1, 4)]
    public async Task ValidChecksumsDoNotPermitImpossibleTransitionSequences(int revision, int state, int operation)
    {
        using var input = new MemoryStream(Recording(new(revision, state, 1001, operation, 0, 0, 0)));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, false));
    }
    [Theory]
    [InlineData(1001, 1, 2, true)]
    [InlineData(1000, 1, 2, false)]
    [InlineData(1001, 0, 2, false)]
    [InlineData(1001, 1, 3, false)]
    [InlineData(1003, 1, 2, false)]
    public async Task HeartbeatsPreservePausedStateWithoutManufacturingEvents(long timestamp, int state, int revision, bool valid)
    {
        var bytes = Recording();
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, true)) { w.Write(timestamp); w.Write(state); w.Write(revision); }
        var data = payload.ToArray(); var header = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, 7); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Crc32.Compute(data, Crc32.Compute(header.AsSpan(0, 8))));
        using var input = new MemoryStream();input.Write(bytes.AsSpan(0, 156));input.Write(header);input.Write(data);input.Write(bytes.AsSpan(156));input.Position = 0;
        var blocks = new List<TraceBlock>();
        var task = NativeTraceFile.FollowAsync(input, b => { blocks.Add(b); return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(1));
        if (!valid) { await Assert.ThrowsAsync<InvalidDataException>(() => task); return; }
        await task;
        Assert.Contains(blocks, b => !b.Recording && b.Events.Length == 0 && b.Symbols.Length == 0);
        Assert.Equal(2, blocks.Sum(b => b.Events.Length));
    }
    [Theory] [InlineData(2)] [InlineData(3)]
    public async Task EarlierVersionsCannotSmuggleControlFrames(int version)
    {
        var bytes = Recording();BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), Crc32.Compute(bytes.AsSpan(0,64)));
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, false));
    }
    private static byte[] Reply(byte[] request, int state = 0)
    {
        var r = new byte[64]; "DVNACK01"u8.CopyTo(r); request.AsSpan(8, 16).CopyTo(r.AsSpan(8)); request.AsSpan(56, 8).CopyTo(r.AsSpan(24));
        request.AsSpan(64, 4).CopyTo(r.AsSpan(32)); BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(40), state);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(56), 123); return r;
    }
    internal static byte[] Recording(NativeControlState? middle = null)
    {
        var original = NativeCaptureCompatibilityTests.Recording();
        BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(64), Crc32.Compute(original.AsSpan(0, 64)));
        using var output = new MemoryStream(); output.Write(original.AsSpan(0, 68));
        foreach (var c in new[] { new NativeControlState(1, 0, 1000, 0, 0, 0, 0), middle ?? new(2, 1, 1001, 1, 0, 0, 0), new(3, 0, 1002, 2, 0, 0, 0) })
        {
            using var payload = new MemoryStream(); using var w = new BinaryWriter(payload); w.Write(c.Revision); w.Write(c.State); w.Write(c.Timestamp); w.Write(c.Operation); w.Write(c.HResult); w.Write(c.Methods); w.Write(c.FailedMethods);
            var data = payload.ToArray(); var h = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(h, 6); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), Crc32.Compute(data, Crc32.Compute(h.AsSpan(0, 8)))); output.Write(h); output.Write(data);
        }
        output.Write(original.AsSpan(68)); return output.ToArray();
    }
}
