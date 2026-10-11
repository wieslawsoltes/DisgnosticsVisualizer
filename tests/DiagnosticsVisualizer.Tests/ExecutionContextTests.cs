using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class ExecutionContextTests
{
    private static Task Schedule(Func<Task> work)
    {
        using var scope = Probe.Enter((int)Subsystem.Application, "schedule", null);
        return Task.Run(work);
    }

    [Fact] public async Task ParentSurvivesAwaitAndConfigureAwaitWithoutLeakingIntoCaller()
    {
        using var session = Probe.Start(new() { FlowExecutionContext = true });
        await Schedule(async () =>
        {
            Probe.Instant(Subsystem.Application, "before await");
            await Task.Delay(1).ConfigureAwait(false);
            Probe.Instant(Subsystem.Application, "after await");
            using var nested = Probe.Enter((int)Subsystem.Application, "nested", null);
            Probe.Instant(Subsystem.Application, "inside nested");
        });
        Probe.Instant(Subsystem.Application, "caller");
        var block = session.Drain();
        TraceEvent Event(string name) => Assert.Single(block.Events, e => block.Symbols.Single(s => s.Id == e.NameId).Name == name);
        var root = Event("schedule");
        Assert.Equal(root.Id, Event("before await").ParentId);
        Assert.Equal(root.Id, Event("after await").ParentId);
        Assert.Equal(root.Id, Event("nested").ParentId);
        Assert.Equal(Event("nested").Id, Event("inside nested").ParentId);
        Assert.Equal(0, Event("caller").ParentId);
        Assert.Equal(0, session.ProbeFaults);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DisabledOrSuppressedFlowDoesNotInventAnEdge(bool enabled)
    {
        using var session = Probe.Start(new() { FlowExecutionContext = enabled });
        Task job;
        using (var scope = Probe.Enter((int)Subsystem.Application, "schedule", null))
        using (ExecutionContext.SuppressFlow())
            job = Task.Run(() => Probe.Instant(Subsystem.Application, "independent"));
        await job;
        var block = session.Drain();
        var name = block.Symbols.Single(s => s.Name == "independent").Id;
        Assert.Equal(0, Assert.Single(block.Events, e => e.NameId == name).ParentId);
        Assert.Equal(0, session.ProbeFaults);
    }

    [Fact] public void ContextRunOnSameThreadDoesNotBorrowThePhysicalCaller()
    {
        using var session = Probe.Start(new() { FlowExecutionContext = true });
        var empty = ExecutionContext.Capture()!;
        using (var scope = Probe.Enter((int)Subsystem.Application, "outer", null))
        {
            ExecutionContext.Run(empty, _ =>
            {
                using var isolated = Probe.Enter((int)Subsystem.Application, "isolated", null);
            }, null);
            Probe.Instant(Subsystem.Application, "restored");
        }
        var b = session.Drain();
        TraceEvent E(string name) => b.Events.Single(e => b.Symbols.Single(s => s.Id == e.NameId).Name == name);
        Assert.Equal(0, E("isolated").ParentId);
        Assert.Equal(E("outer").Id, E("restored").ParentId);
        Assert.Equal(0, session.ProbeFaults);
    }

    [Fact] public async Task FlowFromDisposedSessionCannotContaminateANewSession()
    {
        var old = Probe.Start(new() { FlowExecutionContext = true });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Schedule(async () => { ready.SetResult(); await release.Task; Probe.Instant(Subsystem.Application, "late"); });
        await ready.Task;
        old.Dispose();
        using var current = Probe.Start(new() { FlowExecutionContext = true });
        release.SetResult(); await pending;
        Assert.Equal(0, Assert.Single(current.Drain().Events).ParentId);
    }

    [Fact] public void EnvironmentOptionIsValidated()
    {
        Assert.True(CaptureOptions.FromEnvironment(k => k == "AVTRACE_FLOW_EXECUTION_CONTEXT" ? "1" : null).FlowExecutionContext);
        Assert.Throws<ArgumentException>(() => CaptureOptions.FromEnvironment(k => k == "AVTRACE_FLOW_EXECUTION_CONTEXT" ? "sometimes" : null));
    }
}
