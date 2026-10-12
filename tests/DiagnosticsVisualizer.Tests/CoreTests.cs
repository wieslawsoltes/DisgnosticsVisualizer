using System.Diagnostics;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace DiagnosticsVisualizer.Tests;

public sealed class CoreTests
{
    private static TraceBlock Example() => new()
    {
        SessionId = Guid.Parse("be9baeb4-ea5c-4774-acaf-421f9c15c638"), Sequence = 1,
        OriginTimestamp = 100, Frequency = 1000, UtcStartTicks = 1000,
        Symbols = [new(1, "Measure"), new(2, "Arrange")], Objects = [new(42, "Button", "Button #42")],
        Events = [new(1, 0, 100, 30, 42, 0, 1, 1, Subsystem.Layout),
                  new(2, 1, 105, 5, 42, 0, 1, 2, Subsystem.Layout)],
        Snapshot = new([new(42, 0, 0, 0, "Button", "", 0, 0, 100, 30, true, "")],
            [new(42, "Width", "Styled", "100", "LocalValue", "")], [])
    };
    [Fact] public void BinaryRoundtrip()
    {
        var block = Example(); var copy = TraceCodec.Decode(TraceCodec.Encode(block));
        Assert.Equal(block.Events, copy.Events); Assert.Equal(block.Symbols, copy.Symbols);
        Assert.Equal(block.Objects, copy.Objects); Assert.Equal(block.Snapshot!.Nodes, copy.Snapshot!.Nodes);
        Assert.Equal(block.Snapshot.Properties, copy.Snapshot.Properties); Assert.Equal(block.SessionId, copy.SessionId);
    }
    [Fact] public void EveryTruncationIsRejected()
    {
        var bytes = TraceCodec.Encode(Example());
        for (var i = 0; i < bytes.Length; i++) Assert.Throws<InvalidDataException>(() => TraceCodec.Decode(bytes.AsSpan(0, i)));
    }
    [Fact] public void EverySingleByteCorruptionIsRejected()
    {
        var original = TraceCodec.Encode(Example());
        for (var i = 0; i < original.Length; i++)
        {
            var bytes = original.ToArray(); bytes[i] ^= 0x40;
            Assert.Throws<InvalidDataException>(() => TraceCodec.Decode(bytes));
        }
    }
    [Fact] public async Task TruncatedFinalFileBlockIsRecoverable()
    {
        var frame = TraceCodec.Encode(Example()); using var stream = new MemoryStream();
        stream.Write(frame); stream.Write(frame.AsSpan(0, frame.Length / 2)); stream.Position = 0;
        var result = await TraceFile.ReadAsync(stream, _ => ValueTask.CompletedTask);
        Assert.Equal(1, result.Blocks); Assert.True(result.TruncatedTail);
    }
    [Fact] public void DeltaArithmeticHandlesExtremeIdentifiers()
    {
        var b = Example() with { Events = [new(long.MaxValue, 0, long.MinValue, 0, long.MaxValue, long.MinValue, int.MinValue, int.MaxValue, Subsystem.Application),
            new(long.MinValue, 0, long.MaxValue, 0, long.MinValue, long.MaxValue, int.MaxValue, int.MinValue, Subsystem.Diagnostics)] };
        Assert.Equal(b.Events, TraceCodec.Decode(TraceCodec.Encode(b)).Events);
    }
    [Fact] public void DisabledProbeDoesNotAllocate()
    {
        var allocated = AllocationMeasurement.Measure(static count =>
        { for (var i = 0; i < count; i++) { using var scope = Probe.Enter(0, "test", null); } }, 100_000);
        Assert.Equal(0, allocated);
    }
    [Fact] public void NestedScopesHaveCausalParents()
    {
        using var session = Probe.Start(); var target = new object();
        using (Probe.Enter(0, "outer", target)) { using var inner = Probe.Enter(0, "inner", target); }
        var b = session.Drain(); var innerEvent = b.Events.Single(e => b.Symbols.Single(s => s.Id == e.NameId).Name == "inner");
        var outerEvent = b.Events.Single(e => e.Id == innerEvent.ParentId);
        Assert.Equal(outerEvent.ObjectId, innerEvent.ObjectId); Assert.True(outerEvent.Duration >= innerEvent.Duration);
        Assert.Equal(0, b.ProbeFaults);
    }
    [Fact] public void BoundedBuffersReportLoss()
    {
        using var session = Probe.Start(new() { EventsPerThread = 16 });
        for (var i = 0; i < 100; i++) Probe.Instant(Subsystem.Application, "event");
        var block = session.Drain(); Assert.Equal(16, block.Events.Length); Assert.Equal(84, block.DroppedEvents);
    }
    [Fact] public async Task ParallelProducersKeepUniqueIdsAndReportNoFaults()
    {
        using var session = Probe.Start(new() { EventsPerThread = 4096 });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        { for (var i = 0; i < 500; i++) { using var scope = Probe.Enter(0, "parallel", null); } })));
        var events = new List<TraceEvent>();
        while (true) { var block = session.Drain(); events.AddRange(block.Events); if (block.Events.Length == 0) break; }
        Assert.Equal(4000, events.Count + session.DroppedEvents); Assert.Equal(events.Count, events.Select(e => e.Id).Distinct().Count());
        Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void StoreRejectsMixingClocksAndDeduplicates()
    {
        var store = new TraceStore(); var b = Example(); store.Add(b); store.Add(b); Assert.Equal(2, store.Count);
        Assert.Throws<InvalidDataException>(() => store.Add(b with { SessionId = Guid.NewGuid(), Sequence = 2 }));
    }
    [Fact] public void SelfTimeUsesIntervalUnion()
    {
        var b = Example() with { Events = [new(1, 0, 100, 30, 42, 0, 1, 1, Subsystem.Layout),
            new(2, 1, 105, 15, 42, 0, 1, 2, Subsystem.Layout), new(3, 1, 110, 15, 42, 0, 1, 2, Subsystem.Layout)] };
        var store = new TraceStore(); store.Add(b); var result = TraceAnalyzer.Analyze(store.GetView());
        Assert.Equal(10, result.Costs.Single(x => x.Name == "Measure").SelfMs);
    }
    [Fact] public void IntervalQueryFindsSpansBeginningBeforeViewport()
    {
        var index = new IntervalIndex([new(1, 0, 0, 1000, 0, 0, 1, 1, Subsystem.Layout), new(2, 0, 50, 2, 0, 0, 1, 1, Subsystem.Layout)]);
        Assert.Equal(1, Assert.Single(index.Query(500, 600)).Id);
    }
    [Fact] public void CommandRoundtrip()
    {
        var command = new TraceCommand(7, CommandKind.SelectObject, 42);
        Assert.Equal(command, CommandCodec.Decode(CommandCodec.Encode(command)));
    }
}
