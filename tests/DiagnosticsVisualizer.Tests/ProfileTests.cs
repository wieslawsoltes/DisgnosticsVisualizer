using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class ProfileTests
{
    private static TraceView View(params TraceEvent[] events) => new(Guid.NewGuid(), 1000, 0, events,
        new Dictionary<int, string> { [1] = "Root", [2] = "Measure", [3] = "Arrange" }, new Dictionary<long, ObjectInfo>(), SnapshotData.Empty, 0, 0, 0, 0, 0, false);
    private static TraceEvent E(long id, long parent, long start, long duration, int name = 1, int thread = 1) => new(id, parent, start, duration, 12, 0, thread, name, Subsystem.Layout);
    [Fact]
    public void SelfTimeAndCallingContextsUseRangeClippedSynchronousChildren()
    {
        var view = View(E(1, 0, 0, 100), E(2, 1, 10, 30, 2), E(3, 1, 40, 40, 3), E(4, 1, 20, 80, 2, 2));
        var index = new TraceProfile(view); var all = index.Analyze(); var range = index.Analyze(new(20, 60));
        Assert.Equal(30, index.SelfTicks(view.Events[0], new(0, 100)));
        Assert.Equal(0, index.SelfTicks(view.Events[0], new(20, 60)));
        Assert.Equal(100, all.Threads.Single(t => t.ThreadId == 1).BusyMs);
        Assert.Equal(80, range.Threads.Sum(t => t.BusyMs));
        Assert.Equal(80, range.Activity.Sum(b => b.CpuMs), 6);
        Assert.Equal(180, all.BottomUp.Where(r => r.Depth == 0).Sum(r => r.TotalMs));
        Assert.DoesNotContain(all.TopDown, row => row.Depth > 1);
        Assert.Contains(all.TopDown, row => row.Depth == 0 && row.Subsystem == Subsystem.Layout && row.Operation == "Measure");
    }
    [Fact]
    public void OverlappingChildUnionNeverDoubleSubtracts()
    {
        var view = View(E(1, 0, 0, 100), E(2, 1, 10, 60), E(3, 1, 30, 50));
        Assert.Equal(30, new TraceProfile(view).SelfTicks(view.Events[0], new(0, 100)));
    }
    [Fact]
    public void ActivityMatchesDiscreteUnionForRandomOverlaps()
    {
        var random = new Random(4918);
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var events = Enumerable.Range(1, 100).Select(i => E(i, 0, random.Next(200), random.Next(1, 30), 1, random.Next(1, 5))).ToArray();
            var result = new TraceProfile(View(events)).Analyze(new(20, 170), 7);
            var expected = Enumerable.Range(1, 4).Sum(thread => Enumerable.Range(20, 150).Count(t => events.Any(e => e.ThreadId == thread && e.Timestamp <= t && e.End > t)));
            Assert.Equal(expected, result.Threads.Sum(t => t.BusyMs), 6);
            Assert.Equal(expected, result.Activity.Sum(t => t.CpuMs), 6);
            Assert.All(result.Threads, t => Assert.InRange(t.OccupancyPercent, 0, 100));
        }
    }
    [Fact]
    public void PercentilesUseSortedObservedDurations()
    {
        var profile = new TraceProfile(View(E(1, 0, 0, 10), E(2, 0, 20, 30), E(3, 0, 60, 50))).Analyze();
        var row = Assert.Single(profile.Operations);
        Assert.Equal(30, row.P50Ms); Assert.Equal(48, row.P95Ms, 5); Assert.Equal(49.6, row.P99Ms, 5);
    }
    [Fact]
    public void CyclicTraceCausalityIsBounded()
    {
        var result = new TraceProfile(View(E(1, 2, 0, 100), E(2, 1, 0, 100))).Analyze();
        Assert.True(result.Truncated); Assert.InRange(result.TopDown.Length, 1, 8);
    }
    [Theory]
    [InlineData("subsystem:Layout duration>=10ms", true)]
    [InlineData("name=Root and (thread=1 or thread=2)", true)]
    [InlineData("not name:Root or duration>10s", false)]
    [InlineData("duration<10001us object=12", true)]
    [InlineData("duration<1ns", false)]
    [InlineData("\"Root\" thread!=2", true)]
    public void QueriesRespectPrecedenceAndUnits(string text, bool expected)
    {
        var e = E(1, 0, 0, 10); Assert.Equal(expected, TraceQuery.Compile(text, View(e))(e));
    }
    [Theory]
    [InlineData("unknown:1")]
    [InlineData("name:\"unterminated")]
    [InlineData("thread=wrong")]
    [InlineData("duration>NaN")]
    [InlineData("(Root")]
    [InlineData("Root or")]
    [InlineData("name>Root")]
    public void InvalidQueriesProvidePositions(string text)
    { Assert.InRange(Assert.Throws<TraceQueryException>(() => TraceQuery.Compile(text, View())).Position, 0, text.Length); }
    [Fact]
    public void IdentityComparisonsRetainAll64Bits()
    {
        const long id = 9007199254740993;
        var e = E(id, 0, 0, 10); var view = View(e);
        Assert.True(TraceQuery.Compile($"id={id}", view)(e));
        Assert.False(TraceQuery.Compile($"id={id - 1}", view)(e));
    }
    [Fact]
    public void SnapshotHistoryIsBoundedAndPreservesWatermarks()
    {
        var session = Guid.NewGuid(); var store = new TraceStore();
        for (var i = 1; i <= 100; i++) store.Add(new TraceBlock { SessionId = session, Sequence = i,
            Events = [E(i, 0, i * 10, 5)], Snapshot = SnapshotData.Empty });
        var history = store.GetView().Checkpoints;
        Assert.Equal(32, history.Length); Assert.Equal(100, history[^1].Sequence); Assert.Equal(1005, history[^1].EventWatermark);
    }
}
