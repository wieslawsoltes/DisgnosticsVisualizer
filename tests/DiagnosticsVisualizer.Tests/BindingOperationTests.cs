using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class BindingOperationTests
{
    private sealed class Opaque
    {
        public override string ToString() => throw new InvalidOperationException("Do not read values");
        public override int GetHashCode() => throw new InvalidOperationException("Do not hash values");
        public override bool Equals(object? other) => throw new InvalidOperationException("Do not compare values");
    }
    private static BindingOperationScope Begin(string name, BindingOperationKind kind, object? expression, object? input = null, int facts = 0)
        => Probe.BeginBindingOperation(name, (int)kind, expression, null, null, null, input, facts, long.MinValue);
    private static TraceView View(TraceBlock block) { var store = new TraceStore(); store.Add(block); return store.GetView(); }
    [Fact] public async Task PublicationDeliveryIdentityNullAndExceptionsSurviveExport()
    {
        using var session = Probe.Start(new() { TrackBindingOperations = true }); var expression = new object(); var value = new Opaque();
        var facts = BindingOperationEvent.Facts(BindingValueKind.Value, running: true);
        using (var publish = Begin("publish", BindingOperationKind.Publish, expression, value, facts))
        {
            using (var delivery = Begin("delivery", BindingOperationKind.Deliver, expression, value, facts))
            { Probe.Instant(Subsystem.Properties, "mutation"); delivery.Returned(1, value, facts); }
            publish.Returned(1, value, facts);
        }
        using (var noValue = Begin("null", BindingOperationKind.Convert, expression)) noValue.Returned(1, null, BindingOperationEvent.Facts(BindingValueKind.Null));
        try { using var throwing = Begin("throw", BindingOperationKind.Convert, expression); throw new InvalidOperationException("Original"); }
        catch (InvalidOperationException e) when (e.Message == "Original") { }
        var trace = View(session.Drain()); var index = new BindingOperationIndex(trace);
        Assert.Equal(4, index.Calls.Length); Assert.Equal(3, index.Roots.Length); Assert.Equal(0, index.InvalidMarkers);
        var delivered = Assert.Single(index.Calls, c => c.Kind == BindingOperationKind.Deliver);
        Assert.Equal(session.GetObjectId(value), delivered.InputId); Assert.Equal(delivered.InputId, delivered.OutputId);
        Assert.Equal(delivered.Id, Assert.Single(trace.Events, e => trace.Name(e) == "mutation").ParentId);
        Assert.Contains(index.Calls, c => c.OutputKind == "Null"); Assert.Contains(index.Calls, c => c.Status == "Threw");
        using var stream = new MemoryStream(); await TraceExport.WriteBinaryAsync(stream, trace); stream.Position = 0;
        var store = new TraceStore(); await TraceFile.ReadAsync(stream, block => { store.Add(block); return ValueTask.CompletedTask; });
        Assert.Equal(index.Calls, new BindingOperationIndex(store.GetView()).Calls); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void WholeTreeSamplingPauseSuppressionAndSessionReplacementAreIsolated()
    {
        using (var session = Probe.Start(new() { TrackBindingOperations = true, SampleEvery = 2 }))
        {
            using (var skipped = Begin("skip", BindingOperationKind.Start, this))
            { using var child = Begin("skip child", BindingOperationKind.Publish, this); child.Returned(1, null, 0); skipped.Returned(1, null, 0); }
            using (var admitted = Begin("admit", BindingOperationKind.Start, this))
            {
                session.SetRecording(false);
                using (Probe.Suppress()) { using var hidden = Begin("hidden", BindingOperationKind.Publish, this); hidden.Returned(1, null, 0); }
                using var child = Begin("child", BindingOperationKind.Publish, this); child.Returned(1, null, 0); admitted.Returned(1, null, 0);
            }
            var index = new BindingOperationIndex(View(session.Drain())); Assert.Equal(2, index.Calls.Length); Assert.Equal(0, index.InvalidMarkers); Assert.Equal(0, session.ProbeFaults);
        }
        var old = Probe.Start(new() { TrackBindingOperations = true }); var scope = Begin("old", BindingOperationKind.Start, this); old.Dispose();
        using var fresh = Probe.Start(new() { TrackBindingOperations = true });
        using (var rejected = Begin("nested new", BindingOperationKind.Start, this)) rejected.Returned(1, null, 0);
        scope.Dispose(); using (var independent = Begin("fresh", BindingOperationKind.Start, this)) independent.Returned(1, null, 0);
        Assert.Equal("fresh", Assert.Single(new BindingOperationIndex(View(fresh.Drain())).Calls).Name);
    }
    [Fact] public void MalformedIncompleteReorderedAndCyclicEvidenceNeverBecomesACompleteCall()
    {
        using var session = Probe.Start(new() { TrackBindingOperations = true });
        using (var root = Begin("root", BindingOperationKind.Start, this))
        { using var child = Begin("child", BindingOperationKind.Publish, this); child.Returned(1, null, 0); root.Returned(1, null, 0); }
        var trace = View(session.Drain()); var index = new BindingOperationIndex(trace);
        Assert.Equal(index.Calls, new BindingOperationIndex(trace with { Events = trace.Events.Reverse().ToArray() }).Calls);
        Assert.All(new BindingOperationIndex(trace with { Events = trace.Events.Where(e => !BindingOperationEvent.IsEnd(e)).ToArray() }).Calls, c => { Assert.False(c.IsComplete); Assert.Null(c.DurationMs); });
        var rootId = Assert.Single(index.Roots).Id; var childId = Assert.Single(index.Calls, c => c.ParentOperationId == rootId).Id;
        var cycle = trace.Events.Select(e => BindingOperationEvent.OperationId(e) == rootId && BindingOperationEvent.Phase(e) != 3 ? e with { Data1 = childId } : e).ToArray();
        var cyclic = new BindingOperationIndex(trace with { Events = cycle }); Assert.Equal(2, cyclic.InvalidMarkers); Assert.Equal(2, cyclic.Descendants(rootId).Length);
        var descriptor = trace.Events.First(e => BindingOperationEvent.Phase(e) == 1);
        Assert.True(new BindingOperationIndex(trace with { Events = [..trace.Events, descriptor with { Id = 999 }] }).InvalidMarkers > 0);
        Assert.All(new BindingOperationIndex(trace with { MissingBlocks = 1 }).Calls, c => Assert.True(c.CaptureIncomplete));
    }
    [Fact] public void ExistingSentinelsAreClassifiedByReferenceOnly()
    {
        var unset = new Opaque(); var unchanged = new Opaque(); var nothing = new Opaque();
        Assert.Equal(BindingValueKind.Unset, BindingOperationEvent.ValueKind(Probe.BindingFacts(unset, true, unset, unchanged, nothing, -1, -1, -1, -1, -1)));
        Assert.Equal(BindingValueKind.Unchanged, BindingOperationEvent.ValueKind(Probe.BindingFacts(unchanged, true, unset, unchanged, nothing, -1, -1, -1, -1, -1)));
        Assert.Equal(BindingValueKind.DoNothing, BindingOperationEvent.ValueKind(Probe.BindingFacts(nothing, true, unset, unchanged, nothing, -1, -1, -1, -1, -1)));
        Assert.True(CaptureOptions.FromEnvironment(n => n == "AVTRACE_BINDING_OPERATIONS" ? "1" : null).TrackBindingOperations);
    }
    [Fact] public void WarmRecordedAndPausedBindingIntervalsRetainZeroByteGate()
    {
        using var session = Probe.Start(new() { TrackBindingOperations = true, EventsPerThread = 131072 }); var value = new Opaque();
        var facts = BindingOperationEvent.Facts(BindingValueKind.Value);
        Action<int> call = count => { for (var i = 0; i < count; i++) { using var scope = Begin("hot", BindingOperationKind.Publish, this, value, facts); scope.Returned(1, value, facts); } };
        Assert.Equal(0, AllocationMeasurement.Measure(call)); session.SetRecording(false); Assert.Equal(0, AllocationMeasurement.Measure(call));
        Assert.Equal(0, session.ProbeFaults); Assert.Equal(0, session.DroppedEvents);
    }
}
