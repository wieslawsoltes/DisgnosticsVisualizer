using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class IntegrityExportTests
{
    [Fact] public async Task ReexportPreservesInheritedLossWithoutDoubleCountingOrInventingEvents()
    {
        var store = new TraceStore(8); var session = Guid.NewGuid();
        TraceBlock Batch(long sequence, long id) => new() { SessionId = session, Sequence = sequence, Frequency = 1000,
            Events = Enumerable.Range(0, 8).Select(i => new TraceEvent(id + i, 0, id + i, 1, 0, 0, 1, int.MaxValue, Subsystem.Layout)).ToArray(),
            Symbols = [new(int.MaxValue, "Actual target operation")], DroppedEvents = 3, ProbeFaults = 2, MetadataDrops = 1 };
        store.Add(Batch(1, 1)); store.Add(Batch(3, 10));
        var original = store.GetView(); Assert.Equal(1, original.MissingBlocks); Assert.Equal(8, original.EvictedEvents);
        var view = original;
        for (var i = 0; i < 3; i++)
        {
            using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, view); output.Position = 0;
            var next = new TraceStore(); await TraceFile.ReadAsync(output, b => { next.Add(b); return ValueTask.CompletedTask; });
            view = next.GetView();
            Assert.Equal(original.Events, view.Events); Assert.Equal(1, view.MissingBlocks); Assert.Equal(8, view.EvictedEvents);
            Assert.Equal(3, view.DroppedEvents); Assert.Equal(2, view.ProbeFaults); Assert.Equal(1, view.MetadataDrops);
            Assert.Equal("Actual target operation", view.Name(view.Events[0]));
            Assert.DoesNotContain(view.Symbols.Values, value => value.StartsWith(TraceIntegritySymbol.Prefix, StringComparison.Ordinal));
        }
    }
    [Fact] public void MalformedReservedMetadataCannotPoisonAnAcceptedSequence()
    {
        var store = new TraceStore(); var block = new TraceBlock { SessionId = Guid.NewGuid(), Frequency = 1000, Sequence = 1,
            Symbols = [new(1, TraceIntegritySymbol.Prefix + "1:invalid")] };
        Assert.Throws<InvalidDataException>(() => store.Add(block));
        store.Add(block with { Symbols = [new(1, TraceIntegritySymbol.Encode(new(7, 9)))] });
        var view = store.GetView(); Assert.Equal(7, view.MissingBlocks); Assert.Equal(9, view.EvictedEvents);
        store.Add(block with { Sequence = 3, Symbols = [] });
        Assert.Equal(8, store.GetView().MissingBlocks);
    }
    [Theory]
    [InlineData("-1:0")][InlineData("0:+1")][InlineData("0:1:2")][InlineData("9223372036854775808:0")][InlineData(" 1:1")]
    public void CorruptIntegrityPayloadsAreRejected(string payload) => Assert.False(TraceIntegritySymbol.TryDecode(TraceIntegritySymbol.Prefix + payload, out _));
    [Fact] public void IntegrityCountersSaturateWithoutOverflow()
    {
        var symbol = TraceIntegritySymbol.Encode(new(long.MaxValue, long.MaxValue));
        Assert.True(TraceIntegritySymbol.TryDecode(symbol, out var restored)); Assert.Equal(long.MaxValue, restored.MissingBlocks);
        Assert.Equal(long.MaxValue, TraceIntegritySymbol.SaturatingAdd(restored.MissingBlocks, 1));
    }
}
