using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using DiagnosticsVisualizer.Weaver.Fixtures;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class RoutedEventTests
{
    [Fact] public void CapturedDispatchRecordsHandledSkipsAndSynchronousCausality()
    {
        using var capture = Probe.Start(new() { TrackRoutedEvents = true }); var args = new object(); var target = new object();
        var dispatch = Probe.BeginRoutedEvent(args, target, new object(), "Click", 4, false);
        Action callback = () => { };
        var handler = Probe.BeginRoutedHandler(callback, target, args, target, 4, false, false, false);
        Probe.Instant(Subsystem.Properties, "Mutation", target);
        Probe.EndRoutedHandler(ref handler, true, true);
        Probe.RoutedCandidate(callback, target, args, target, 4, 4, true, false);
        Probe.EndRoutedEvent(ref dispatch, true, true);
        var trace = View(capture.Drain()); var row = Assert.Single(RoutedDispatches.Analyze(trace));
        Assert.Equal("Returned", row.Status); Assert.True(row.HandledAfter); Assert.Equal(2, row.Steps.Length);
        Assert.True(row.Steps[0].ChangedHandled); Assert.Equal("Skipped: handled", row.Steps[1].Status);
        Assert.Equal(row.Steps[0].EventId, Assert.Single(trace.Events, e => trace.Name(e) == "Mutation").ParentId);
        Assert.Equal(0, capture.ProbeFaults);
        var costs = TraceAnalyzer.Analyze(trace).Costs; Assert.True(costs.Sum(c => c.SelfMs) <= row.DurationMs + .00001);
    }

    [Fact] public void NestedUnsampledRaiseWithSameArgsCannotLeakIntoOuterDispatch()
    {
        using var capture = Probe.Start(new() { TrackRoutedEvents = true, SampleEvery = 2 }); var args = new object();
        var ignored = Probe.BeginRoutedEvent(args, args, args, "ignored", 4, false); Probe.EndRoutedEvent(ref ignored, false, true);
        var outer = Probe.BeginRoutedEvent(args, args, args, "outer", 4, false);
        var inner = Probe.BeginRoutedEvent(args, args, args, "not-sampled", 4, false);
        var h = Probe.BeginRoutedHandler(null, args, args, args, 4, false, false, true); Probe.EndRoutedHandler(ref h, false, true);
        Probe.EndRoutedEvent(ref inner, false, true);
        h = Probe.BeginRoutedHandler(null, args, args, args, 4, false, false, true); Probe.EndRoutedHandler(ref h, false, true);
        Probe.EndRoutedEvent(ref outer, false, true);
        var row = Assert.Single(RoutedDispatches.Analyze(View(capture.Drain()))); Assert.Equal("outer", row.Name); Assert.Single(row.Steps);
    }

    [Fact] public void PausedDispatchFinishesButDisposedSessionCannotContaminateReplacement()
    {
        var first = Probe.Start(new() { TrackRoutedEvents = true }); var args = new object();
        var scope = Probe.BeginRoutedEvent(args, args, args, "pause", 1, false); first.SetRecording(false);
        Probe.EndRoutedEvent(ref scope, false, true); Assert.Equal("Returned", Assert.Single(RoutedDispatches.Analyze(View(first.Drain()))).Status);
        first.SetRecording(true); scope = Probe.BeginRoutedEvent(args, args, args, "old", 1, false); first.Dispose();
        using var replacement = Probe.Start(new() { TrackRoutedEvents = true });
        Probe.EndRoutedEvent(ref scope, false, true); Probe.Instant(Subsystem.Application, "new");
        Assert.Equal(0, Assert.Single(replacement.Drain().Events).ParentId);
    }

    [Fact] public void WarmEnabledAndDisabledRouteScopesHaveNoManagedAllocations()
    {
        using var capture = Probe.Start(new() { TrackRoutedEvents = true, EventsPerThread = 65536 }); var args = new object(); Action callback = () => { };
        void Iteration()
        {
            var route = Probe.BeginRoutedEvent(args, args, args, "warm", 4, false);
            var h = Probe.BeginRoutedHandler(callback, args, args, args, 4, false, false, false);
            Probe.EndRoutedHandler(ref h, false, true); Probe.EndRoutedEvent(ref route, false, true);
        }
        for (var i = 0; i < 256; i++) Iteration();
        var before = GC.GetAllocatedBytesForCurrentThread(); for (var i = 0; i < 10000; i++) Iteration();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before; Assert.Equal(0, allocated);
        capture.SetRecording(false); before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) Iteration();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before; Assert.Equal(0, allocated);
        Assert.Equal(0, capture.ProbeFaults); Assert.Equal(0, capture.DroppedEvents);
    }

    [Fact] public async Task ActualRewrittenRouteLoopPreservesOrderExceptionsAndNullContracts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "routed-weave-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var fixture = typeof(RoutingSubject).Assembly.Location;
        foreach (var file in new[] { fixture, typeof(Avalonia.Interactivity.EventRoute).Assembly.Location }) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var context = new Context(directory);
        try
        {
            var manifest = WeaveEngine.Weave(new(directory) { Include = [Path.GetFileName(fixture)], Deep = true, Bootstrap = false, AllowSignedRewrite = true });
            Assert.Equal(2, manifest.Assemblies.SelectMany(a => a.Methods).Count(m => m.Status == "RoutedEventDispatch"));
            using var capture = Probe.Start(new() { TrackRoutedEvents = true, EventsPerThread = 65536 });
            var type = context.LoadFromAssemblyPath(Path.Combine(directory, Path.GetFileName(fixture))).GetType(typeof(RoutingSubject).FullName!)!;
            var result = (string)type.GetMethod(nameof(RoutingSubject.Exercise))!.Invoke(null, null)!;
            Assert.Equal("root-tunnel,leaf-too,leaf-bubble,nested,root-bubble,typed-fault,dynamic-fault,null-source,null-args", result);
            var trace = View(capture.Drain()); var rows = RoutedDispatches.Analyze(trace);
            Assert.Equal(4, rows.Length); Assert.Equal(2, rows.Count(r => r.Status == "Threw"));
            var outer = Assert.Single(rows, r => r.Name == "RoutingProbe");
            Assert.Contains(outer.Steps, s => s.Status == "Skipped: handled"); Assert.Contains(outer.Steps, s => s.Status == "Skipped: route");
            Assert.Equal(2, outer.ChangedHandled); Assert.False(outer.HandledAfter);
            Assert.Contains(outer.Steps, s => s.Kind == "Class listeners"); Assert.Equal(0, capture.ProbeFaults);
            using var stream = new MemoryStream(); await TraceExport.WriteBinaryAsync(stream, trace); stream.Position = 0;
            var store = new TraceStore(); await TraceFile.ReadAsync(stream, b => { store.Add(b); return ValueTask.CompletedTask; });
            Assert.Equal(rows.Select(r => (r.Id, r.Status, r.Steps.Length)), RoutedDispatches.Analyze(store.GetView()).Select(r => (r.Id, r.Status, r.Steps.Length)));
        }
        finally { context.Unload(); Directory.Delete(directory, true); }
    }
    private sealed class Context(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(Probe).Assembly.GetName().Name) return typeof(Probe).Assembly;
            if (name.Name == typeof(Subsystem).Assembly.GetName().Name) return typeof(Subsystem).Assembly;
            var path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private static TraceView View(TraceBlock block) { var store = new TraceStore(); store.Add(block); return store.GetView(); }
}
