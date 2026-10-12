using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class RetainedCaptureTests
{
    private static TraceBlock Block(Guid session, long sequence, params TraceEvent[] events) => new()
    { SessionId = session, Sequence = sequence, Frequency = 1000, Events = events };
    private static TraceEvent Event(long id, long? timestamp = null) => new(id, 0, timestamp ?? id, 1, 0, 0, 1, 0, Subsystem.Application);

    [Fact] public void EvictionRetainsTheExactTailInsteadOfDiscardingWholeBatches()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(5);
        store.Add(Block(session, 1, Event(1), Event(2), Event(3)));
        var before = store.GetView();
        store.Add(Block(session, 2, Event(4), Event(5), Event(6), Event(7)));
        var after = store.GetView();
        Assert.Equal(new long[] { 3, 4, 5, 6, 7 }, after.Events.Select(e => e.Id));
        Assert.Equal(5, store.Count); Assert.Equal(2, after.EvictedEvents);
        Assert.Equal(new long[] { 1, 2, 3 }, before.Events.Select(e => e.Id));
        store.Add(Block(session, 3, Enumerable.Range(100, 11).Select(i => Event(i)).ToArray()));
        Assert.Equal(new long[] { 106, 107, 108, 109, 110 }, store.GetView().Events.Select(e => e.Id));
        Assert.Equal(13, store.GetView().EvictedEvents);
    }

    [Fact] public void OversizedBatchStillRetainsLastEventsAndOnlyCountsAcceptedIdentities()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(3);
        var events = Enumerable.Range(1, 10_000).Select(i => Event(i)).ToArray();
        store.Add(Block(session, 1, events.Concat(events).ToArray()));
        Assert.Equal(new long[] { 9998, 9999, 10000 }, store.GetView().Events.Select(e => e.Id));
        Assert.Equal(9997, store.GetView().EvictedEvents);
    }

    [Fact] public void OutOfOrderArrivalsMergeWithoutSharingMutablePublishedArrays()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(20);
        var input = new[] { Event(5, 20), Event(2, 20), Event(1, long.MinValue), Event(9, long.MaxValue - 1) };
        store.Add(Block(session, 1, input)); input[0] = Event(900, 0);
        var first = store.GetView(); Assert.Equal(new long[] { 1, 2, 5, 9 }, first.Events.Select(e => e.Id));
        first.Events[0] = Event(800, 0);
        store.Add(Block(session, 2, Event(7, 19), Event(3, 20), Event(4, -1)));
        var expected = new long[] { 1, 4, 7, 2, 3, 5, 9 };
        Assert.Equal(expected, store.GetView().Events.Select(e => e.Id));
        var second = store.GetView(); second.Events[0] = default;
        Assert.Equal(expected, store.GetView().Events.Select(e => e.Id));
    }

    [Fact] public void ReintroducedEvictedIdDoesNotResurrectCachedPayload()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(2);
        store.Add(Block(session, 1, Event(1, 1), Event(2, 2))); _ = store.GetView();
        store.Add(Block(session, 2, Event(3, 3))); // Retire cached 1.
        store.Add(Block(session, 3, Event(1, 100))); // New arrival after eviction.
        Assert.Equal(new long[] { 3, 1 }, store.GetView().Events.Select(e => e.Id));
        Assert.Equal(100, store.GetView().Events[^1].Timestamp);
        Assert.Equal(2, store.GetView().EvictedEvents);
    }

    [Fact] public void RandomBatchesAndPublicationCadencesMatchSimpleArrivalModel()
    {
        var random = new Random(1776); var session = Guid.NewGuid(); var store = new TraceStore(31);
        var retained = new List<TraceEvent>(); long evicted = 0;
        for (var sequence = 1; sequence <= 500; sequence++)
        {
            var batch = Enumerable.Range(0, random.Next(0, 60)).Select(_ => Event(random.Next(1, 200), random.Next(-500, 501))).ToArray();
            var ids = retained.Select(e => e.Id).ToHashSet();
            retained.AddRange(batch.Where(e => ids.Add(e.Id)));
            if (retained.Count > 31) { var remove = retained.Count - 31; evicted += remove; retained.RemoveRange(0, remove); }
            store.Add(Block(session, sequence, batch));
            if (sequence % 3 == 0) store.Add(Block(session, sequence, batch)); // Sequence replay.
            if (sequence % 7 == 0 || sequence == 500)
            {
                var view = store.GetView();
                Assert.Equal(retained.OrderBy(e => e.Timestamp).ThenBy(e => e.Id), view.Events);
                Assert.Equal(evicted, view.EvictedEvents); Assert.Equal(retained.Count, store.Count);
                if (view.Events.Length > 0) view.Events[0] = default;
            }
        }
    }

    [Fact] public void RetiredSequenceGapsRemainCountedAndOldReplayCannotReenter()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(100, sequenceCapacity: 3);
        foreach (var sequence in new[] { 1, 3, 5, 6, 7 }) store.Add(Block(session, sequence, Event(sequence)));
        Assert.Equal(2, store.GetView().MissingBlocks);
        store.Add(Block(session, 2, Event(200))); // Below retired horizon: cannot repair gap or add events.
        Assert.Equal(2, store.GetView().MissingBlocks); Assert.Equal(5, store.Count);
        store.Add(Block(session, 4, Event(4))); // Still repairable inside the active horizon.
        Assert.Equal(1, store.GetView().MissingBlocks); Assert.Equal(6, store.Count);
        store.Add(Block(session, 20, Event(20)));
        Assert.Equal(13, store.GetView().MissingBlocks);
        store.Add(Block(session, 1, Event(900))); Assert.DoesNotContain(store.GetView().Events, e => e.Id == 900);
    }

    [Fact] public void LateJoinDoesNotInventLossBeforeItsFirstObservedSequence()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(20, sequenceCapacity: 2);
        foreach (var sequence in new[] { 100, 101, 103, 104 }) store.Add(Block(session, sequence, Event(sequence)));
        Assert.Equal(1, store.GetView().MissingBlocks);
        store.Add(Block(session, 102, Event(102))); Assert.Equal(0, store.GetView().MissingBlocks);
        store.Add(Block(session, 1, Event(1))); Assert.Equal(0, store.GetView().MissingBlocks); Assert.Equal(5, store.Count);
    }

    [Fact] public async Task ExtremeSequenceValuesAndExportPreserveRetiredGaps()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(10, sequenceCapacity: 2);
        foreach (var sequence in new[] { long.MaxValue - 4, long.MaxValue - 2, long.MaxValue, long.MaxValue - 1 })
            store.Add(Block(session, sequence, Event(sequence, 1)));
        Assert.Equal(1, store.GetView().MissingBlocks);
        using var stream = new MemoryStream(); await TraceExport.WriteBinaryAsync(stream, store.GetView()); stream.Position = 0;
        var restored = new TraceStore(); await TraceFile.ReadAsync(stream, b => { restored.Add(b); return ValueTask.CompletedTask; });
        Assert.Equal(1, restored.GetView().MissingBlocks);
    }

    [Fact] public void InvalidCapacitiesAndSequenceDoNotMutateTheStore()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceStore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceStore(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceStore(1, 1_000_001));
        var store = new TraceStore();
        Assert.Throws<InvalidDataException>(() => store.Add(Block(Guid.NewGuid(), 0, Event(1))));
        Assert.Equal(Guid.Empty, store.GetView().SessionId); Assert.Equal(0, store.Count);
        store.Add(Block(Guid.NewGuid(), 1, Event(2))); Assert.Equal(2, Assert.Single(store.GetView().Events).Id);
    }

    [Fact] public void PublishedMetadataIsReadOnlyReusedAndDoesNotAliasLaterUpdates()
    {
        var session = Guid.NewGuid(); var store = new TraceStore();
        var block = Block(session, 1, Event(1)) with { Symbols = [new(1, "Name")], Objects = [new(10, "Type", "First")] };
        store.Add(block); var first = store.GetView();
        store.Add(block with { Sequence = 2, Events = [] }); var unchanged = store.GetView();
        Assert.Same(first.Symbols, unchanged.Symbols); Assert.Same(first.Objects, unchanged.Objects); Assert.Same(first.SourceSites, unchanged.SourceSites);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, string>)first.Symbols).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<long, ObjectInfo>)first.Objects)[10] = new(10, "Type", "Mutated"));
        store.Add(block with { Sequence = 3, Events = [], Symbols = [new(2, "Other")], Objects = [new(10, "Type", "Second")] });
        var changed = store.GetView();
        Assert.Equal("First", first.Objects[10].Display); Assert.Equal("Second", changed.Objects[10].Display);
        Assert.Single(first.Symbols); Assert.Equal(2, changed.Symbols.Count);
        Assert.NotSame(first.Symbols, changed.Symbols); Assert.NotSame(first.Objects, changed.Objects);
    }

    [Fact] public async Task ConcurrentIngestionAndPublicationProduceBoundedConsistentViews()
    {
        var session = Guid.NewGuid(); var store = new TraceStore(127); long next = 0;
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var sequence = Interlocked.Increment(ref next);
                store.Add(Block(session, sequence, Enumerable.Range(0, 8).Select(j => Event(sequence * 8 + j, -sequence)).ToArray()));
                var view = store.GetView();
                Assert.InRange(view.Events.Length, 1, 127);
                Assert.Equal(view.Events.Length, view.Events.Select(e => e.Id).Distinct().Count());
                Assert.Equal(view.Events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id), view.Events);
            }
        }));
        await Task.WhenAll(writers);
        Assert.Equal(127, store.Count); Assert.Equal(3200 - 127, store.GetView().EvictedEvents); Assert.Equal(0, store.GetView().MissingBlocks);
    }
}
