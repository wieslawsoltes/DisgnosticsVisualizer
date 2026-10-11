using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class ProbeContractTests
{
    [Fact]
    public void WarmEnabledScopesDoNotAllocate()
    {
        using var session = Probe.Start(new() { EventsPerThread = 16384 });
        var target = new object();
        for (var i = 0; i < 256; i++) { using var scope = Probe.Enter(0, "hot", target); }
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { using var scope = Probe.Enter(0, "hot", target); }
        // Evaluate before entering xUnit generic assertion helpers (which may initialize lazily).
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
        Assert.Equal(0, session.ProbeFaults);
        Assert.Equal(0, session.DroppedEvents);
    }
    [Fact]
    public void SuppressionIsNestedAndRestoresTheRecordedParent()
    {
        using var session = Probe.Start();
        using (Probe.Enter(0, "outer", null))
        {
            using (Probe.Suppress())
            {
                Probe.Instant(Subsystem.Application, "hidden");
                using (Probe.Suppress()) { using var hidden = Probe.Enter(0, "also hidden", null); }
                Probe.Instant(Subsystem.Application, "still hidden");
            }
            using var child = Probe.Enter(0, "child", null);
        }
        var block = session.Drain();
        Assert.Equal(2, block.Events.Length);
        var outer = block.Events.Single(e => e.ParentId == 0);
        Assert.Contains(block.Events, e => e.ParentId == outer.Id);
        Assert.Equal(0, block.ProbeFaults);
    }
    [Fact]
    public void NullNotificationArgumentsCannotChangeApplicationBehavior()
    {
        using var session = Probe.Start();
        Probe.PropertyNotification(this, null);
        Probe.CollectionNotification(this, null);
        Assert.Equal(2, session.Drain().Events.Length);
        Assert.Equal(0, session.ProbeFaults);
    }
    [Fact]
    public void InvalidPublicEnumsCannotPoisonTheCapturePump()
    {
        using var session = Probe.Start();
        Probe.Instant((Subsystem)255, "invalid");
        Probe.Instant(Subsystem.Application, "invalid kind", kind: (TraceEventKind)255);
        using (Probe.Enter(-1, "invalid scope", null)) { }
        Assert.Empty(session.Drain().Events);
    }
    [Fact]
    public void DoubleDisposalDoesNotCorruptParentState()
    {
        using var session = Probe.Start();
        var scope = Probe.Enter(0, "once", null);
        scope.Dispose(); scope.Dispose();
        using (Probe.Enter(0, "next", null)) { }
        var block = session.Drain();
        Assert.Equal(2, block.Events.Length);
        Assert.All(block.Events, e => Assert.Equal(0, e.ParentId));
        Assert.Equal(1, block.ProbeFaults);
    }
    [Fact]
    public void ExplicitContextConnectsARealCrossThreadContinuation()
    {
        using var session = Probe.Start();
        TraceContext token;
        using (Probe.Enter(0, "producer", null)) token = TraceContext.Capture();
        var thread = new Thread(() =>
        {
            using (token.Resume()) { using var scope = Probe.Enter(0, "consumer", null); }
            using var independent = Probe.Enter(0, "independent", null);
        });
        thread.Start(); thread.Join();
        var block = session.Drain();
        var producer = block.Events.Single(e => e.Id == token.ParentEventId);
        var consumer = block.Events.Single(e => e.ParentId == producer.Id);
        Assert.NotEqual(producer.ThreadId, consumer.ThreadId);
        Assert.Equal(2, block.Events.Count(e => e.ParentId == 0));
        Assert.Equal(0, block.ProbeFaults);
    }
    [Fact]
    public void ContextFromADisposedSessionCannotAttachToANewCapture()
    {
        TraceContext token;
        using (var old = Probe.Start())
        { using var scope = Probe.Enter(0, "old", null); token = TraceContext.Capture(); }
        using var session = Probe.Start();
        using (token.Resume()) { using var scope = Probe.Enter(0, "new", null); }
        Assert.Equal(0, Assert.Single(session.Drain().Events).ParentId);
    }
}
