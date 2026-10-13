using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;
using Xunit;
namespace DiagnosticsVisualizer.Tests;

public sealed class NativeBootstrapTests
{
    [Fact] public async Task BootstrapKeepsAllNativeDefinitionsAfterEventTailEviction()
    {
        var cache = new NativeBootstrapCache(); var frames = new List<TraceBlock>();
        using var stream = new MemoryStream(NativeCaptureTests.Recording());
        await NativeTraceFile.ReadAsync(stream, block => { frames.Add(block); cache.Observe(block, TraceCodec.Encode(block)); return ValueTask.CompletedTask; });
        for (var i=10; i<3000; i++) { var b = frames.First(f => f.Events.Length != 0) with { Sequence = i }; cache.Observe(b, TraceCodec.Encode(b)); }
        var replay = cache.Snapshot().Select(b => TraceCodec.Decode(b)).ToArray();
        Assert.All(replay, b => Assert.Empty(b.Events));
        Assert.Contains(replay.SelectMany(b=>b.Symbols), s=>s.Name.StartsWith(SourceSiteSymbol.Prefix));
        Assert.Equal(4, replay.SelectMany(b=>b.Symbols).Count(s=>s.Name.StartsWith(NativeMetadataSymbol.Prefix)));
    }
    [Fact] public void SessionChangeClearsProvenanceAndMetadataBudgetIsEnforced()
    {
        var cache = new NativeBootstrapCache(); var b = new TraceBlock { SessionId = Guid.NewGuid(), Frequency=1000, Sequence=1,
            Symbols=[new(1, NativeMetadataSymbol.Encode(new NativeProcessInfo(2,8)))] };
        cache.Observe(b, TraceCodec.Encode(b)); Assert.Single(cache.Snapshot());
        var other = b with { SessionId=Guid.NewGuid(), Symbols=[] }; cache.Observe(other, TraceCodec.Encode(other)); Assert.Empty(cache.Snapshot());
        var bounded = new NativeBootstrapCache(16);
        Assert.Throws<InvalidDataException>(()=>bounded.Observe(b, TraceCodec.Encode(b))); Assert.Empty(bounded.Snapshot());
    }
    [Fact] public void CacheOwnsInputAndRejectsDifferentContentAtSameSourceSequence()
    {
        var cache = new NativeBootstrapCache(); var b = new TraceBlock { SessionId=Guid.NewGuid(), Frequency=1000, Sequence=1,
            Symbols=[new(1, NativeMetadataSymbol.Encode(new NativeProcessInfo(2,8)))] };
        var bytes = TraceCodec.Encode(b); cache.Observe(b, bytes); bytes[0]^=1;
        Assert.Equal(TraceCodec.Encode(b), Assert.Single(cache.Snapshot()));
        var exposed = Assert.Single(cache.Snapshot()); exposed[0] ^= 1;
        Assert.Equal(TraceCodec.Encode(b), Assert.Single(cache.Snapshot()));
        var changed = b with { Symbols=[new(1, NativeMetadataSymbol.Encode(new NativeProcessInfo(3,8)))] };
        Assert.Throws<InvalidDataException>(()=>cache.Observe(changed, TraceCodec.Encode(changed)));
    }
}
