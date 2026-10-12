using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class DispatcherOperationTests
{
    [Fact] public void QueueContextFlowsToCallbackWithoutExecutionContextFlow()
    {
        using var session = Probe.Start(new() { TrackDispatcherOperations = true }); var operation = new object();
        using (Probe.Enter((int)Subsystem.Application, "producer", null)) Probe.DispatcherQueued(operation, 4, -6, "admission");
        var thread = new Thread(() =>
        {
            using var scope = Probe.DispatcherStarted(operation, 7, "callback");
            Probe.Instant(Subsystem.Properties, "update", operation);
        }); thread.Start(); thread.Join();
        var store = new TraceStore(); store.Add(session.Drain()); var trace = store.GetView();
        var row = Assert.Single(DispatcherOperations.Analyze(trace));
        Assert.Equal("Returned", row.Status); Assert.NotEqual(row.PostingThread, row.ExecutionThread);
        Assert.True(row.QueueMs >= 0); Assert.True(row.ExecutionMs >= 0);
        Assert.Equal(4, row.PostedPriority); Assert.Equal(7, row.ExecutionPriority);
        var child = Assert.Single(trace.Events, e => trace.Name(e) == "update");
        Assert.Equal(row.Started!.Value.Id, child.ParentId);
        Assert.Equal(0, session.ProbeFaults);
    }

    [Fact] public void FaultAbortAndPausedCompletionAreDistinct()
    {
        using var session = Probe.Start(new() { TrackDispatcherOperations = true });
        var faulted = new object(); Probe.DispatcherQueued(faulted, 1, -6, "admission");
        using (Probe.DispatcherStarted(faulted, 1, "callback")) Probe.DispatcherFault(new InvalidOperationException("not captured"), faulted);
        var aborted = new object(); Probe.DispatcherQueued(aborted, 2, -6, "admission");
        Probe.DispatcherAborted(aborted); Probe.DispatcherAborted(aborted);
        var paused = new object(); Probe.DispatcherQueued(paused, 3, -6, "admission"); session.SetRecording(false);
        using (Probe.DispatcherStarted(paused, 3, "callback")) { }
        var store = new TraceStore(); store.Add(session.Drain()); var rows = DispatcherOperations.Analyze(store.GetView());
        Assert.Equal(3, rows.Length); Assert.Contains(rows, r => r.Status == "Callback threw");
        Assert.Contains(rows, r => r.Status == "Returned"); var abort = Assert.Single(rows, r => r.Status == "Aborted before execution");
        Assert.Null(abort.Started); Assert.Null(abort.ExecutionMs); Assert.True(abort.QueueMs >= 0);
        Assert.Equal(0, session.ProbeFaults);
    }

    [Fact] public void DisabledSuppressedAndDisposedSessionsDoNotInventStarts()
    {
        var operation = new object();
        using (var session = Probe.Start(new() { TrackDispatcherOperations = true }))
        {
            session.SetRecording(false); Probe.DispatcherQueued(operation, 0, -6, "admission"); session.SetRecording(true);
            using (Probe.DispatcherStarted(operation, 0, "callback")) { }
            using (Probe.Suppress()) Probe.DispatcherQueued(operation, 0, -6, "suppressed");
            Assert.Empty(session.Drain().Events);
            Probe.DispatcherQueued(operation, 0, -6, "old session");
        }
        using var replacement = Probe.Start(new() { TrackDispatcherOperations = true });
        using (Probe.DispatcherStarted(operation, 0, "new session")) { }
        Assert.Empty(replacement.Drain().Events);
        Assert.True(CaptureOptions.FromEnvironment(k => k == "AVTRACE_DISPATCHER_OPERATIONS" ? "true" : null).TrackDispatcherOperations);
    }

    [Fact] public void InactiveTimerResidenceIsNotRunnableQueueDelay()
    {
        using var session = Probe.Start(new() { TrackDispatcherOperations = true }); var operation = new object();
        Probe.DispatcherQueued(operation, -6, -6, "timer admitted");
        Thread.Sleep(5); Probe.DispatcherPriorityChanged(operation, 0);
        using (Probe.DispatcherStarted(operation, 0, "timer callback")) { }
        var store = new TraceStore(); store.Add(session.Drain()); var trace = store.GetView();
        var row = Assert.Single(DispatcherOperations.Analyze(trace));
        Assert.True(row.InactiveQueueMs > 0); Assert.True(row.RunnableQueueMs >= 0);
        Assert.Equal(row.QueueMs!.Value, row.InactiveQueueMs!.Value + row.RunnableQueueMs!.Value, 6);
        Assert.Single(row.PriorityChanges); Assert.Equal(-6, row.PostedPriority);
        var lossy = Assert.Single(DispatcherOperations.Analyze(trace with { DroppedEvents = 1 }));
        Assert.NotNull(lossy.QueueMs); Assert.Null(lossy.RunnableQueueMs); Assert.Null(lossy.InactiveQueueMs);
    }

    [Fact] public void QueuedWorkItemsAreWeakAndDisabledHooksDoNotAllocate()
    {
        using var session = Probe.Start(new() { TrackDispatcherOperations = true });
        var weak = AdmitAndForget(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
        session.SetRecording(false); var operation = new object();
        Probe.DispatcherQueued(operation, 0, -6, "disabled");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) Probe.DispatcherQueued(operation, 0, -6, "disabled");
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(0, session.ProbeFaults);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AdmitAndForget()
    {
        var operation = new object(); Probe.DispatcherQueued(operation, 0, -6, "weak admission"); return new(operation);
    }

    [Fact] public void MissingAndConflictingMarkersRemainUnknown()
    {
        static TraceView View(params TraceEvent[] events)
        {
            var store = new TraceStore(); store.Add(new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Events = events }); return store.GetView();
        }
        var q = new TraceEvent(1, 0, 10, 0, 42, 0, 1, 1, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.QueuedCode, 1);
        var s = q with { Id = 2, ParentId = 1, Timestamp = 20, Data0 = DispatcherEvent.StartedCode };
        var f = q with { Id = 3, ParentId = 1, Timestamp = 30, Data0 = DispatcherEvent.FinishedCode, Data1 = 1 };
        Assert.Equal("Incomplete / not retained", Assert.Single(DispatcherOperations.Analyze(View(q))).Status);
        var row = Assert.Single(DispatcherOperations.Analyze(View(s, f))); Assert.Null(row.QueueMs); Assert.NotNull(row.ExecutionMs);
        row = Assert.Single(DispatcherOperations.Analyze(View(q, s, f with { Timestamp = 19 })));
        Assert.Equal("Conflicting / invalid markers", row.Status); Assert.Null(row.QueueMs); Assert.Null(row.ExecutionMs);
        row = Assert.Single(DispatcherOperations.Analyze(View(q, s, f, f with { Id = 4 })));
        Assert.Equal("Conflicting / invalid markers", row.Status);
        row = Assert.Single(DispatcherOperations.Analyze(View(q with { Data1 = long.MaxValue }, s, f)));
        Assert.Equal("Conflicting / invalid markers", row.Status);
    }
}
