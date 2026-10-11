using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class AsyncOperationTests
{
    private const int Category = (int)Subsystem.Application;
    private static TraceView View(TraceBlock block) { var store = new TraceStore(); store.Add(block); return store.GetView(); }

    [Fact] public void DisabledThenEnabledDoesNotInventAStart()
    {
        var token = new AsyncOperationToken();
        Probe.AsyncBegin(ref token, Category, "before recording", null);
        using var session = Probe.Start(new() { TrackAsyncOperations = true });
        Probe.AsyncBegin(ref token, Category, "after resume", null); Probe.AsyncComplete(ref token, null);
        Assert.Empty(session.Drain().Events);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void DefaultAndSuppressedModesAreInert(bool suppressed)
    {
        using var session = Probe.Start(new() { TrackAsyncOperations = suppressed });
        var token = new AsyncOperationToken();
        using (Probe.Suppress()) Probe.AsyncBegin(ref token, Category, "hidden", null);
        Probe.AsyncComplete(ref token, null);
        var other = new AsyncOperationToken();
        if (!suppressed) { Probe.AsyncBegin(ref other, Category, "disabled", null); Probe.AsyncComplete(ref other, null); }
        Assert.Empty(session.Drain().Events);
    }

    [Fact] public async Task CompletionSurvivesPauseAndDoesNotPollutePhysicalOrAmbientParents()
    {
        using var session = Probe.Start(new() { TrackAsyncOperations = true, FlowExecutionContext = true });
        var token = new AsyncOperationToken();
        using (Probe.Enter(Category, "caller", null)) Probe.AsyncBegin(ref token, Category, "work", null);
        session.SetRecording(false);
        await Task.Run(() => { Probe.AsyncComplete(ref token, null); Probe.AsyncComplete(ref token, null); });
        var trace = View(session.Drain()); var op = Assert.Single(AsyncOperations.Analyze(trace));
        Assert.Equal("Succeeded", op.Status); Assert.True(op.DurationMs >= 0);
        Assert.Equal(trace.Events.Single(e => e.Kind == TraceEventKind.Span).Id, op.Begin!.Value.ParentId);
        Assert.Equal(0, session.ProbeFaults);
        session.SetRecording(true); Probe.Instant(Subsystem.Application, "after");
        Assert.Equal(0, Assert.Single(session.Drain().Events).ParentId);
    }

    [Fact] public void CompletionCannotCrossSessionIdentity()
    {
        var token = new AsyncOperationToken();
        using (var old = Probe.Start(new() { TrackAsyncOperations = true })) Probe.AsyncBegin(ref token, Category, "old", null);
        using var session = Probe.Start(new() { TrackAsyncOperations = true });
        Probe.AsyncComplete(ref token, null); Assert.Empty(session.Drain().Events);
    }

    [Fact] public void MissingAndConflictingMarkersNeverBecomeSuccessfulZeroDurationOperations()
    {
        var start = new TraceEvent(1, 0, 100, 0, 0, 0, 1, 1, Subsystem.Application, TraceEventKind.Link, AsyncOperationEvent.BeginCode);
        var end = start with { Id = 2, ParentId = 1, Timestamp = 200, Data0 = AsyncOperationEvent.EndCode, Data1 = 1 };
        AsyncOperation Result(params TraceEvent[] events) => Assert.Single(AsyncOperations.Analyze(View(new() { SessionId = Guid.NewGuid(), Sequence = 1, Events = events })));
        Assert.Null(Result(start).DurationMs); Assert.Equal("Completion not recorded", Result(start).Status);
        Assert.Null(Result(end).DurationMs); Assert.Equal("Start not retained", Result(end).Status);
        Assert.Equal("Conflicting markers", Result(start, end, end with { Id = 3 }).Status);
        Assert.Equal("Invalid lifecycle", Result(start, end with { Timestamp = 50 }).Status);
        Assert.Equal("Invalid lifecycle", Result(start, end with { NameId = 2 }).Status);
        Assert.Equal("Invalid lifecycle", Result(start, end with { Data1 = 99 }).Status);
        Assert.Equal("Succeeded", Result(end, start).Status); // UDP order is not lifecycle order.
    }

    private sealed class FixtureContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == typeof(Probe).Assembly.GetName().Name ? typeof(Probe).Assembly :
            name.Name == typeof(Subsystem).Assembly.GetName().Name ? typeof(Subsystem).Assembly : null;
    }

    [Fact] public async Task WovenStateMachinesPreserveTaskValueTaskGenericsCancellationAndSingleConsumption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "avtrace-async-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = typeof(DiagnosticsVisualizer.Weaver.Fixtures.AsyncSubject).Assembly.Location;
        var target = Path.Combine(directory, Path.GetFileName(source)); File.Copy(source, target);
        if (File.Exists(Path.ChangeExtension(source, ".pdb"))) File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(target, ".pdb"));
        var context = new FixtureContext();
        try
        {
            var manifest = WeaveEngine.Weave(new(directory) { Deep = true, Include = [Path.GetFileName(target)], Bootstrap = false });
            Assert.True(manifest.Assemblies.Single().Methods.Count(m => m.Status == "AsyncLifecycle") == 7,
                string.Join("\n", manifest.Assemblies.Single().Methods.Select(m => m.Signature + ": " + m.Status + " " + m.Note)));
            using var session = Probe.Start(new() { TrackAsyncOperations = true, FlowExecutionContext = true });
            var assembly = context.LoadFromAssemblyPath(target);
            var type = assembly.GetType("DiagnosticsVisualizer.Weaver.Fixtures.AsyncSubject")!; var instance = Activator.CreateInstance(type);
            object Call(string method, params object[] args) => type.GetMethod(method)!.Invoke(instance, args)!;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = (Task<int>)Call("TaskAsync", gate.Task, 42);
            var store = new TraceStore(); store.Add(session.Drain());
            Assert.Equal("Completion not recorded", Assert.Single(AsyncOperations.Analyze(store.GetView())).Status);
            Assert.False(task.IsCompleted); gate.SetResult(); Assert.Equal(42, await task);
            Assert.Equal(57, await (ValueTask<int>)Call("ValueAsync", Task.CompletedTask, 57));
            await (ValueTask)Call("EmptyValueAsync", Task.CompletedTask);
            var original = new InvalidOperationException("original");
            Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)Call("FailAsync", Task.CompletedTask, original)));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var canceled = (Task)Call("FailAsync", Task.CompletedTask, new OperationCanceledException(cancellation.Token));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.True(canceled.IsCanceled); Assert.Equal(cancellation.Token, error.CancellationToken);
            var generic = assembly.GetType("DiagnosticsVisualizer.Weaver.Fixtures.GenericAsyncSubject`1")!.MakeGenericType(typeof(int));
            Assert.Equal(64, await (Task<int>)generic.GetMethod("EchoAsync")!.MakeGenericMethod(typeof(string))
                .Invoke(Activator.CreateInstance(generic), [Task.CompletedTask, 64, "argument"])!);
            var singleType = assembly.GetType("DiagnosticsVisualizer.Weaver.Fixtures.SingleUseSource")!;
            var single = Activator.CreateInstance(singleType)!;
            var once = (ValueTask<int>)Call("SingleUseAsync", single);
            singleType.GetMethod("Complete")!.Invoke(single, [73]); Assert.Equal(73, await once);
            Assert.Equal(1, singleType.GetField("Consumptions")!.GetValue(single));
            var done = false; Call("VoidAsync", Task.CompletedTask, (Action)(() => done = true)); Assert.True(done);
            store.Add(session.Drain());
            var trace = store.GetView(); var operations = AsyncOperations.Analyze(trace);
            Assert.Equal(8, operations.Length); Assert.All(operations, op => Assert.True(op.IsComplete, op.Name + " " + op.Status));
            Assert.Single(operations, op => op.Status == "Faulted"); Assert.Single(operations, op => op.Status == "Canceled");
            Assert.Equal(0, session.ProbeFaults); Assert.Equal(0, session.DroppedEvents);
            Assert.DoesNotContain(TraceAnalyzer.Analyze(trace).Costs, cost => cost.Name.Contains("MoveNext"));
            using var exported = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(exported, trace);
            using var json = JsonDocument.Parse(exported.ToArray());
            Assert.Equal(8, json.RootElement.GetProperty("traceEvents").EnumerateArray().Count(e => e.GetProperty("ph").GetString() == "b"));
            Assert.Equal(8, json.RootElement.GetProperty("traceEvents").EnumerateArray().Count(e => e.GetProperty("ph").GetString() == "e"));
        }
        finally { context.Unload(); Directory.Delete(directory, true); }
    }

    [Fact] public void LifecycleEnvironmentOptionIsValidated()
    {
        Assert.True(CaptureOptions.FromEnvironment(k => k == "AVTRACE_ASYNC_OPERATIONS" ? "true" : null).TrackAsyncOperations);
        Assert.Throws<ArgumentException>(() => CaptureOptions.FromEnvironment(k => k == "AVTRACE_ASYNC_OPERATIONS" ? "auto" : null));
    }
}
