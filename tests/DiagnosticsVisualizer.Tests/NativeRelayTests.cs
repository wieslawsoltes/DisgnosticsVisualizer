using System.Threading.Channels;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class NativeRelayTests
{
    [Fact] public async Task FollowsPartialWritesAndRepliesToControlsBeforeFooter()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dvntrace");
        try
        {
            var bytes = NativeCaptureTests.Recording();
            await using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            await writer.WriteAsync(bytes.AsMemory(0, bytes.Length - 92)); await writer.FlushAsync();
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            await using var peer = new Peer(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            peer.Commands.Writer.TryWrite(CommandCodec.Encode(new(7, CommandKind.Ping)));
            peer.Commands.Writer.TryWrite(CommandCodec.Encode(new(8, CommandKind.Pause)));
            var relay = NativeCaptureRelay.RunAsync(source, peer, TimeSpan.FromSeconds(2), deadline.Token);
            await peer.Events.Task.WaitAsync(deadline.Token); await peer.Control.Task.WaitAsync(deadline.Token);
            Assert.False(relay.IsCompleted);
            // The checksum/header and payload may each be split across physical writes.
            for (var n = bytes.Length - 92; n < bytes.Length; n += 7)
            { await writer.WriteAsync(bytes.AsMemory(n, Math.Min(7, bytes.Length - n))); await writer.FlushAsync(); }
            var result = await relay; Assert.False(result.TruncatedTail); Assert.Equal(2, result.Events);
            Assert.Contains(peer.Responses, r => r.RequestId == 7 && r.Argument == "PONG");
            Assert.Contains(peer.Responses, r => r.RequestId == 8 && r.Argument == "UNSUPPORTED_NATIVE_CONTROL");
            Assert.False(new NativeCaptureIndex(peer.Store.GetView()).Incomplete);
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task ReconnectReplaysStableIdentitiesWithoutDuplicateEvents()
    {
        var peer = new Peer(failAt: 4); var peer2 = new Peer(store: peer.Store); var count = 0;
        var result = await NativeCaptureRelay.RunWithReconnectAsync(() => new MemoryStream(NativeCaptureTests.Recording()),
            _ => Task.FromResult<IFrameConnection>(++count == 1 ? peer : peer2), TimeSpan.FromSeconds(1), 1);
        Assert.Equal(2, result.Connections); Assert.Equal(1, result.Reconnects);
        Assert.Equal(2, peer.Store.Count); Assert.Equal(0, peer.Store.GetView().MissingBlocks);
        Assert.True(peer.Disposed); Assert.True(peer2.Disposed);
    }
    [Fact] public async Task ReconnectCannotSwitchSourceIdentity()
    {
        var peer = new Peer(failAt: 4); var count = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeCaptureRelay.RunWithReconnectAsync(() =>
        {
            var bytes = NativeCaptureTests.Recording();
            if (count == 2)
            {
                bytes[40] ^= 1;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), Crc32.Compute(bytes.AsSpan(0, 64)));
            }
            return new MemoryStream(bytes);
        }, _ => Task.FromResult<IFrameConnection>(++count == 1 ? peer : new Peer()), TimeSpan.FromSeconds(1), 1));
    }
    [Fact] public async Task NoFooterTimesOutAndCancellationDoesNotClaimSuccess()
    {
        await using var peer = new Peer(); using var source = new MemoryStream(NativeCaptureTests.Recording(footer: false));
        await Assert.ThrowsAsync<TimeoutException>(() => NativeCaptureRelay.RunAsync(source, peer, TimeSpan.FromMilliseconds(100)));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeCaptureRelay.RunWithReconnectAsync(
            () => new MemoryStream(NativeCaptureTests.Recording()), _ => Task.FromResult<IFrameConnection>(new Peer()), TimeSpan.FromSeconds(1), cancellationToken: stop.Token));
    }
    [Fact] public async Task LateJoinReceivesNamesOsThreadsAndFooterWithoutInventedGaps()
    {
        var cache = new NativeMetadataCache(); var blocks = new List<TraceBlock>();
        using var source = new MemoryStream(NativeCaptureTests.Recording());
        await NativeTraceFile.ReadAsync(source, b => { blocks.Add(b); cache.Observe(b); return ValueTask.CompletedTask; }, false);
        var store = new TraceStore();
        foreach (var b in cache.MaterializeBefore(100)) store.Add(b);
        store.Add(blocks.Single(b => b.Events.Length > 0) with { Sequence = 100 });
        var trace = store.GetView(); var index = new NativeCaptureIndex(trace);
        Assert.Equal(0, trace.MissingBlocks); Assert.Equal(123, index.Process!.ProcessId); Assert.Equal(12345UL, index.Threads[0].OsThreadId);
        Assert.Equal(1, index.PairedSpans); Assert.Equal("IL supplied", index.Methods[0].Status);
        cache.Reset(); Assert.Empty(cache.MaterializeBefore(100));
    }
    [Fact] public void InvalidCacheFrameCannotPublishPartialProvenance()
    {
        var cache = new NativeMetadataCache();
        Assert.Throws<InvalidDataException>(() => cache.Observe(new TraceBlock
        {
            SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000,
            Symbols = [new(100, NativeMetadataSymbol.Encode(new NativeProcessInfo(123, 8))), new(101, NativeMetadataSymbol.Prefix + "[9]")]
        }));
        Assert.False(cache.Active); Assert.Empty(cache.MaterializeBefore(100));
    }
    private sealed class Peer(int failAt = -1, TraceStore? store = null) : IFrameConnection
    {
        public readonly TraceStore Store = store ?? new();
        public readonly Channel<byte[]> Commands = Channel.CreateUnbounded<byte[]>();
        public readonly List<TraceCommand> Responses = [];
        public readonly TaskCompletionSource Events = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Control = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        private int _sent;
        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            if (WireEnvelope.GetKind(frame.Span) == FrameKind.Trace)
            {
                if (Interlocked.Increment(ref _sent) == failAt) throw new IOException("Injected network failure");
                var block = TraceCodec.Decode(frame.Span); Store.Add(block); if (block.Events.Length > 0) Events.TrySetResult();
            }
            else { var command = CommandCodec.Decode(frame.Span); lock (Responses) Responses.Add(command); if (command.RequestId == 8) Control.TrySetResult(); }
        }
        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) => await Commands.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
