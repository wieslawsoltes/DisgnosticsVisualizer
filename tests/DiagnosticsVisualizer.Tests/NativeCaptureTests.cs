using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class NativeCaptureTests
{
    internal static byte[] Recording(bool footer = true, bool unwind = false)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write("DVNTRC01"u8); w.Write(2u); w.Write(0u); w.Write(1_000_000_000L); w.Write(0L); w.Write(638000000000000000L);
        w.Write(Guid.Parse("84527711-30b2-400c-876c-68660aeb4211").ToByteArray()); w.Write(100u); w.Write(8u);
        w.Write(Crc32.Compute(stream.ToArray()));
        void Text(BinaryWriter b, string value) { var text = Encoding.UTF8.GetBytes(value); b.Write(text.Length); b.Write(text); }
        void Frame(uint kind, Action<BinaryWriter> content)
        {
            using var buffer = new MemoryStream(); using var b = new BinaryWriter(buffer, Encoding.UTF8, true); content(b);
            var data = buffer.ToArray(); var head = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(head, kind); BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), data.Length);
            w.Write(head); w.Write(Crc32.Compute(data, Crc32.Compute(head))); w.Write(data);
        }
        Frame(1, b => { b.Write(1u); b.Write(Guid.Parse("2817d5f1-d02b-407a-8e91-609774590e76").ToByteArray()); b.Write(0x06000001); Text(b, "NativeTest"); Text(b, "NativeTest.Work"); });
        Frame(2, b => { b.Write(1u); b.Write(789UL); });
        Frame(3, b => { b.Write(2u); foreach (var phase in new[] { 0u, unwind ? 2u : 1u }) { b.Write((1L << 48) + 1); b.Write(0L); b.Write(10L); b.Write(phase == 0 ? 0L : 3L); b.Write(1u); b.Write(1u); b.Write(phase); b.Write(0u); } });
        Frame(4, b => { b.Write(1u); b.Write(2); b.Write(0); b.Write(1u); });
        if (footer) Frame(5, b => { b.Write(0u); b.Write(0u); foreach (var n in new long[] { 20, 0, 0, 0, 1, 1, 1, 0, 0 }) b.Write(n); });
        return stream.ToArray();
    }
    private static async Task<TraceView> Import(byte[] bytes, bool recover = false)
    {
        using var stream = new MemoryStream(bytes); var store = new TraceStore();
        await TraceImport.ReadAsync(stream, b => { store.Add(b); return ValueTask.CompletedTask; }, recover);
        return store.GetView();
    }
    [Fact] public async Task NativeProvenanceSurvivesBinaryAndChromeExport()
    {
        var view = await Import(Recording(unwind: true)); var index = new NativeCaptureIndex(view);
        Assert.Equal(1, index.PairedSpans); Assert.Equal(0, index.InvalidGroups); Assert.False(index.Incomplete);
        Assert.Equal(1, Assert.Single(index.Methods).Unwound); Assert.Equal(789UL, Assert.Single(index.Threads).OsThreadId);
        Assert.DoesNotContain(view.Symbols.Values, s => s.StartsWith(NativeMetadataSymbol.Prefix));
        using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, view);
        var restored = await Import(output.ToArray()); Assert.Equal(view.Events, restored.Events); Assert.Equal(view.UtcStartTicks, restored.UtcStartTicks);
        Assert.Equal(view.NativeMetadata.OrderBy(p => p.Key), restored.NativeMetadata.OrderBy(p => p.Key));
        using var chrome = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(chrome, restored);
        using var json = JsonDocument.Parse(chrome.ToArray());
        Assert.Equal(4, json.RootElement.GetProperty("nativeCapture").GetArrayLength());
        Assert.Contains("OS thread 789 (native slot 1)", Encoding.UTF8.GetString(chrome.ToArray()));
        Assert.Contains("Unwound", Encoding.UTF8.GetString(chrome.ToArray()));
    }
    [Fact] public async Task EveryTruncationAndByteMutationFailsStrictImport()
    {
        var original = Recording();
        for (var i = 0; i < original.Length; i++)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Import(original[..i]));
            var bad = original.ToArray(); bad[i] ^= 0x20;
            await Assert.ThrowsAsync<InvalidDataException>(() => Import(bad));
        }
    }
    [Fact] public async Task ExplicitLegacyHeaderRemainsReadableWithoutPretendingItHasAChecksum()
    {
        var current = Recording();
        var old = current[..64].Concat(current[68..]).ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(8), 1);
        var trace = await Import(old); Assert.Equal(1, new NativeCaptureIndex(trace).Process!.FormatVersion);
        Assert.Equal(1, new NativeCaptureIndex(trace).PairedSpans);
    }
    [Fact] public async Task FooterCannotMakeMissingNativeEventsLookComplete()
    {
        var trace = await Import(Recording());
        Assert.True(new NativeCaptureIndex(trace with { Events = [] }).Incomplete);
    }
    [Fact] public async Task MissingFooterRemainsUnknownAcrossExport()
    {
        var view = await Import(Recording(false), true); var index = new NativeCaptureIndex(view);
        Assert.True(index.Incomplete); Assert.False(index.End!.HasFooter); Assert.True(index.End.TruncatedTail);
        using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, view);
        Assert.True(new NativeCaptureIndex(await Import(output.ToArray())).Incomplete);
    }
    [Fact] public async Task UnexecutedDefinitionAndLossProvenanceSurviveExport()
    {
        var view = await Import(Recording());
        var sites = view.SourceSites.ToDictionary(p => p.Key, p => p.Value); sites[2] = sites[1] with { Token = 0x06000002, Signature = "NeverExecuted" };
        view = view with { SourceSites = sites, MissingBlocks = 3, EvictedEvents = 4 };
        using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, view); var restored = await Import(output.ToArray());
        var index = new NativeCaptureIndex(restored); Assert.Equal(2, index.Methods.Length);
        Assert.Equal(0, index.Methods.Single(m => m.SiteId == 2).Completions); Assert.True(index.Incomplete);
        Assert.Equal(3, restored.MissingBlocks); Assert.Equal(4, restored.EvictedEvents);
    }
    [Fact] public void ConflictingNativeIdentityRejectsBatchWithoutChangingState()
    {
        var session = Guid.NewGuid(); var store = new TraceStore();
        TraceBlock Block(long sequence, params Symbol[] symbols) => new() { SessionId = session, Sequence = sequence, Frequency = 1000, Symbols = symbols };
        store.Add(Block(1, new Symbol(1, NativeMetadataSymbol.Encode(new NativeProcessInfo(10, 8)))));
        var prior = store.GetView();
        Assert.Throws<InvalidDataException>(() => store.Add(Block(2, new Symbol(2, NativeMetadataSymbol.Encode(new NativeProcessInfo(11, 8))))));
        Assert.Same(prior.NativeMetadata, store.GetView().NativeMetadata);
        var fresh = new TraceStore();
        Assert.Throws<InvalidDataException>(() => fresh.Add(Block(1,
            new(1, NativeMetadataSymbol.Encode(new NativeProcessInfo(10, 8))), new(2, NativeMetadataSymbol.Encode(new NativeProcessInfo(11, 8))))));
        Assert.Equal(Guid.Empty, fresh.GetView().SessionId);
    }
    [Fact] public void OutOfOrderStatusCannotRollBackMetadataOrMutatePriorViews()
    {
        var session = Guid.NewGuid(); var store = new TraceStore();
        TraceBlock Block(int seq, int status) => new() { SessionId = session, Sequence = seq, Frequency = 1000,
            Symbols = [new(seq, NativeMetadataSymbol.Encode(new NativeMethodInfo(1, status, 0, 1)))] };
        store.Add(Block(3, 2)); var view = store.GetView(); store.Add(Block(2, 1));
        Assert.Equal(2, ((NativeMethodInfo)store.GetView().NativeMetadata[(3,1)]).Status);
        store.Add(Block(4, -3)); Assert.Equal(2, ((NativeMethodInfo)view.NativeMetadata[(3,1)]).Status);
    }
    [Fact] public async Task FollowerPublishesBeforeFooterAndNeverTreatsQuietFileAsCompleted()
    {
        var path = Path.GetTempFileName(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await File.WriteAllBytesAsync(path, Recording(false), timeout.Token);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var follow = NativeTraceFile.FollowAsync(stream, b => { if (b.Events.Length != 0) observed.TrySetResult(); return ValueTask.CompletedTask; }, TimeSpan.FromSeconds(2), timeout.Token);
            await observed.Task.WaitAsync(timeout.Token); Assert.False(follow.IsCompleted);
            await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                await append.WriteAsync(Recording().AsMemory(Recording(false).Length), timeout.Token);
            Assert.False((await follow).TruncatedTail);
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task StalledFollowerFailsInsteadOfInventingACompleteRecording()
    {
        using var source = new MemoryStream(Recording(false));
        await Assert.ThrowsAsync<TimeoutException>(() => NativeTraceFile.FollowAsync(source, _ => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(100)));
    }
    private sealed class Connection(TraceStore store, int failAfter = int.MaxValue) : IFrameConnection
    {
        private int _sent;
        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return null; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (++_sent > failAfter) throw new IOException("Simulated disconnect");
            store.Add(TraceCodec.Decode(frame.Span)); return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact] public async Task BoundedReplayPreservesSequencesAndDeduplicatesDeliveredEvents()
    {
        using var stream = new MemoryStream(Recording()); var store = new TraceStore(); var connections = 0;
        var result = await NativeRelayClient.RunAsync(stream, _ => Task.FromResult<IFrameConnection>(new Connection(store, ++connections == 1 ? 4 : int.MaxValue)),
            new() { RetryDelay = TimeSpan.Zero });
        Assert.Equal(2, result.Attempts); Assert.True(result.ReplayedFrames > 0);
        Assert.Equal((await Import(Recording())).Events, store.GetView().Events); Assert.Equal(0, store.GetView().MissingBlocks);
    }
    [Fact] public async Task ReconnectRejectsMutationOfPreviouslyDecodedFrames()
    {
        var data = Recording(); using var source = new MemoryStream(data, true); var store = new TraceStore(); var attempts = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeRelayClient.RunAsync(source, _ =>
        {
            if (++attempts == 2) { data[48] ^= 1; BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), Crc32.Compute(data.AsSpan(0,64))); }
            return Task.FromResult<IFrameConnection>(new Connection(store, 4));
        }, new() { RetryDelay = TimeSpan.Zero }));
        Assert.Equal(2, attempts);
    }
    [Fact] public async Task ReplayBudgetAndAuthenticationFailureDoNotRetry()
    {
        using var source = new MemoryStream(Recording()); var attempts = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeRelayClient.RunAsync(source,
            _ => { attempts++; return Task.FromResult<IFrameConnection>(new Connection(new TraceStore())); }, new() { MaximumReplayFrames = 1 }));
        Assert.Equal(1, attempts); source.Position = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NativeRelayClient.RunAsync(source,
            _ => { attempts++; throw new UnauthorizedAccessException(); }));
        Assert.Equal(2, attempts);
    }
    [Fact] public async Task RealTcpAuthenticationAndFramingPreserveNativeCapture()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var token = NetworkSecurity.CreateToken(); var store = new TraceStore();
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var c = new StreamFrameConnection(tcp.GetStream());
            Assert.True(await ConnectionAuthentication.AuthenticateServerAsync(c, token, true, timeout.Token));
            while (await c.ReceiveAsync(timeout.Token) is { } bytes) store.Add(TraceCodec.Decode(bytes));
        }, timeout.Token);
        using var source = new MemoryStream(Recording());
        var result = await NativeRelayClient.RelayAsync(source, new($"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), token, new() { MaximumAttempts = 1 }, timeout.Token);
        await server; Assert.Equal(2, result.Capture.Events); Assert.Equal(1, new NativeCaptureIndex(store.GetView()).PairedSpans);
    }
}
