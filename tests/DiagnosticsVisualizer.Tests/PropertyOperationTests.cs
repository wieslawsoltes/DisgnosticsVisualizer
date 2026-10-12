using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class PropertyOperationTests
{
    private sealed class Opaque { public override string ToString() => throw new InvalidOperationException("Do not format"); }
    private readonly record struct OpaqueValue(int Value) { public override string ToString() => throw new InvalidOperationException("Do not format"); }
    internal static TraceView Read(CaptureSession session)
    { var store = new TraceStore(); while (true) { var b = session.Drain(); store.Add(b); if (b.Events.Length == 0) return store.GetView(); } }
    [Fact] public void ValuesArePrivateByDefaultAndMissingValuesStayUnknown()
    {
        using var session = Probe.Start(new() { TrackPropertyOperations = true }); var property = new object();
        using (var s = Probe.BeginPropertyOperation("set", 1, this, property, "Number", null, 0, 0)) { s.Value(3, 123); s.Value(4, "private password"); s.Returned(); }
        using (var s = Probe.BeginPropertyOperation("unset", 1, this, property, "Number", null, 0, 0)) s.Returned();
        var trace = Read(session); var rows = new PropertyOperationIndex(trace).Calls;
        Assert.Equal(PropertyValueKind.Redacted, rows[0].OldValue.Kind); Assert.Equal(PropertyValueKind.Redacted, rows[0].NewValue.Kind);
        Assert.Equal(PropertyValueKind.Unknown, rows[1].OldValue.Kind); Assert.DoesNotContain(trace.Objects.Values, o => o.Type == typeof(string).FullName);
        Assert.All(trace.Events, e => Assert.True(PropertyOperationEvent.IsValid(e))); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public async Task ScalarBitsAndOpaqueIdentityRoundTripWithoutApplicationCallbacks()
    {
        using var session = Probe.Start(new() { TrackPropertyOperations = true, CapturePropertyScalars = true }); var opaque = new Opaque();
        using (var s = Probe.BeginPropertyOperation("number", 1, this, this, "Test", null, 0, 0)) { s.Value(3, double.NaN); s.Value(4, ulong.MaxValue); s.Returned(); }
        using (var s = Probe.BeginPropertyOperation("object", 1, this, this, "Test", null, 0, 0)) { s.Value(3, opaque); s.Value(4, new OpaqueValue(42)); s.Returned(); }
        using (var s = Probe.BeginPropertyOperation("boxed", 1, this, this, "Test", null, 0, 0)) { s.Value<object>(3, 3.5); s.Value<string?>(4, null); s.Returned(); }
        var trace = Read(session); var rows = new PropertyOperationIndex(trace).Calls;
        Assert.True(double.IsNaN(BitConverter.Int64BitsToDouble(rows[0].OldValue.Bits))); Assert.Equal(-1, rows[0].NewValue.Bits);
        Assert.Equal(session.GetObjectId(opaque), rows[1].OldValue.ObjectId); Assert.Equal(PropertyValueKind.Redacted, rows[1].NewValue.Kind);
        Assert.Equal(3.5, BitConverter.Int64BitsToDouble(rows[2].OldValue.Bits)); Assert.Equal(PropertyValueKind.Null, rows[2].NewValue.Kind);
        using var bytes = new MemoryStream(); await TraceExport.WriteBinaryAsync(bytes, trace); bytes.Position = 0;
        var restored = new TraceStore(); await TraceFile.ReadAsync(bytes, b => { restored.Add(b); return ValueTask.CompletedTask; });
        Assert.Equal(rows, new PropertyOperationIndex(restored.GetView()).Calls); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void NestedPauseSuppressionAndSessionReplacementRemainIsolated()
    {
        var old = Probe.Start(new() { TrackPropertyOperations = true });
        var outer = Probe.BeginPropertyOperation("outer", 1, this, this, "Test", null, 0, 0); old.SetRecording(false);
        using (var nested = Probe.BeginPropertyOperation("nested", 6, this, this, "Test", null, 0, 8)) nested.Returned();
        using (Probe.Suppress()) { using var hidden = Probe.BeginPropertyOperation("hidden", 1, this, this, "Test", null, 0, 0); }
        outer.Returned(); outer.Dispose(); var index = new PropertyOperationIndex(Read(old)); Assert.Equal(2, index.Calls.Length);
        Assert.Equal(index.Calls[0].Id, index.Calls[1].ParentOperationId); Assert.True(index.Calls[1].Effective);
        old.SetRecording(true); var stale = Probe.BeginPropertyOperation("stale", 1, this, this, "Test", null, 0, 0); old.Dispose();
        using var fresh = Probe.Start(new() { TrackPropertyOperations = true });
        using (var orphan = Probe.BeginPropertyOperation("orphan", 1, this, this, "Test", null, 0, 0)) orphan.Returned();
        stale.Dispose(); using (var next = Probe.BeginPropertyOperation("next", 1, this, this, "Test", null, 0, 0)) next.Returned();
        Assert.Equal("next", Assert.Single(new PropertyOperationIndex(Read(fresh)).Calls).Name); Assert.Equal(0, fresh.ProbeFaults);
    }
    [Fact] public void PairingRejectsConflictingAndMissingEvidence()
    {
        using var session = Probe.Start(new() { TrackPropertyOperations = true });
        using (var s = Probe.BeginPropertyOperation("set", 1, this, this, "Test", null, 0, 0)) s.Returned();
        var trace = Read(session); Assert.True(Assert.Single(new PropertyOperationIndex(trace).Calls).IsComplete);
        var shuffled = trace with { Events = trace.Events.Reverse().ToArray() }; Assert.Equal(new PropertyOperationIndex(trace).Calls, new PropertyOperationIndex(shuffled).Calls);
        var begin = trace.Events.Single(e => PropertyOperationEvent.Phase(e) == 1);
        var conflict = new PropertyOperationIndex(trace with { Events = [..trace.Events, begin with { Id = long.MaxValue }] });
        Assert.Equal(1, conflict.InvalidMarkers); Assert.Null(conflict.Calls[0].DurationMs); Assert.Equal(PropertyValueKind.Unknown, conflict.Calls[0].NewValue.Kind);
        var missing = new PropertyOperationIndex(trace with { Events = trace.Events.Where(e => PropertyOperationEvent.Phase(e) != 2).ToArray() });
        Assert.False(Assert.Single(missing.Calls).IsComplete); Assert.Null(missing.Calls[0].DurationMs);
        Assert.True(new PropertyOperationIndex(trace with { MissingBlocks = 1 }).Calls[0].CaptureIncomplete);
    }
    [Fact] public void WarmGenericPrimitiveAndPausedPathsHaveZeroAllocations()
    {
        using var session = Probe.Start(new() { TrackPropertyOperations = true, CapturePropertyScalars = true, EventsPerThread = 131072 });
        Action<int> action = count => { for (var i = 0; i < count; i++) { using var s = Probe.BeginPropertyOperation("hot", 1, this, this, "Test", null, 0, 0); s.Value(3, i); s.Value(4, i + 1); s.Returned(); } };
        Assert.Equal(0, AllocationMeasurement.Measure(action)); session.SetRecording(false); Assert.Equal(0, AllocationMeasurement.Measure(action));
        Assert.Equal(0, session.ProbeFaults); Assert.Equal(0, session.DroppedEvents);
    }
}
