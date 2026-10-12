using System.Reflection;
using System.Runtime.Loader;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using DiagnosticsVisualizer.Weaver.Fixtures;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class StyleEvaluationTests
{
    [Fact] public void OutcomesAndNestedCausalityAreObservedWithoutConflatingConditionalAndActive()
    {
        using var session = Probe.Start(new() { TrackStyleEvaluations = true }); var activator = new object(); var control = new object();
        using (var attach = Probe.BeginStyleEvaluation("attach", 3, this, control, null, false))
        {
            using (var match = Probe.BeginStyleEvaluation("match", 1, this, control, null, true))
            { Probe.Instant(Subsystem.Properties, "child", control); match.Returned(3, activator, false); }
            attach.Returned(3, null, false);
        }
        using (var frame = Probe.BeginStyleEvaluation("frame", 4, new object(), null, this, false)) frame.Returned(6, activator, false);
        var trace = View(session.Drain()); var index = new StyleEvaluationIndex(trace);
        Assert.Equal(3, index.Calls.Length); Assert.Equal(2, index.Roots.Length);
        var matchRow = Assert.Single(index.Calls, r => r.Kind == StyleEvaluationKind.Match);
        var frameRow = Assert.Single(index.Calls, r => r.Kind == StyleEvaluationKind.Frame);
        Assert.Equal("Conditional (activator)", matchRow.Status); Assert.Equal("Inactive", frameRow.Status);
        Assert.Equal(matchRow.ActivatorId, frameRow.ActivatorId); Assert.False(frameRow.Changed);
        Assert.Equal(matchRow.Id, Assert.Single(trace.Events, e => trace.Name(e) == "child").ParentId);
        Assert.Equal(2, index.Descendants(index.RootOf(matchRow.Id)!.Id).Length);
        Assert.Equal(0, index.InvalidMarkers); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void WholeTreeSamplingPausedCompletionAndSuppressionAreIsolated()
    {
        using var session = Probe.Start(new() { TrackStyleEvaluations = true, SampleEvery = 2 });
        using (var ignored = Probe.BeginStyleEvaluation("ignored", 3, this, this, null, false))
        { using var child = Probe.BeginStyleEvaluation("ignored child", 1, this, this, null, true); child.Returned(3, null, false); ignored.Returned(3, null, false); }
        using (var root = Probe.BeginStyleEvaluation("admitted", 3, this, this, null, false))
        {
            session.SetRecording(false);
            using (Probe.Suppress())
            { using var hidden = Probe.BeginStyleEvaluation("hidden", 1, this, this, null, true); hidden.Returned(3, null, false); }
            using var child = Probe.BeginStyleEvaluation("child", 1, this, this, null, true); child.Returned(5, null, false); root.Returned(5, null, false);
        }
        var index = new StyleEvaluationIndex(View(session.Drain()));
        Assert.Equal(2, index.Calls.Length); Assert.Equal("admitted", Assert.Single(index.Roots).Name); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void MissingConflictingOutOfOrderAndCyclicEvidenceStaysUnknown()
    {
        using var session = Probe.Start(new() { TrackStyleEvaluations = true });
        using (var root = Probe.BeginStyleEvaluation("root", 3, this, this, null, false))
        { using var child = Probe.BeginStyleEvaluation("child", 1, this, this, null, true); child.Returned(5, null, false); root.Returned(5, null, false); }
        var trace = View(session.Drain()); var valid = new StyleEvaluationIndex(trace); var rootId = Assert.Single(valid.Roots).Id;
        var childId = Assert.Single(valid.Calls, r => r.ParentEvaluationId == rootId).Id;
        Assert.Equal(valid.Calls, new StyleEvaluationIndex(trace with { Events = trace.Events.Reverse().ToArray() }).Calls);
        var partial = new StyleEvaluationIndex(trace with { Events = trace.Events.Where(e => !StyleEvaluationEvent.IsEnd(e)).ToArray() });
        Assert.All(partial.Calls, r => { Assert.Null(r.DurationMs); Assert.True(r.CaptureIncomplete); });
        var cyclic = new StyleEvaluationIndex(trace with { Events = trace.Events.Select(e => StyleEvaluationEvent.EvaluationId(e) == rootId ? e with { Data1 = childId } : e).ToArray() });
        Assert.Equal(2, cyclic.InvalidMarkers); Assert.Equal(2, cyclic.Descendants(rootId).Length);
        Assert.All(new StyleEvaluationIndex(trace with { MissingBlocks = 1 }).Calls, r => Assert.True(r.CaptureIncomplete));
        var descriptor = trace.Events.First(e => StyleEvaluationEvent.IsBegin(e));
        Assert.True(new StyleEvaluationIndex(trace with { Events = [..trace.Events, descriptor with { Id = 999 }] }).InvalidMarkers > 0);
    }
    [Fact] public void OldSessionCannotAcquireNewSessionChildren()
    {
        var old = Probe.Start(new() { TrackStyleEvaluations = true });
        var scope = Probe.BeginStyleEvaluation("old", 1, this, this, null, true); old.Dispose();
        using var fresh = Probe.Start(new() { TrackStyleEvaluations = true });
        using (var child = Probe.BeginStyleEvaluation("orphan", 1, this, this, null, true)) child.Returned(5, null, false);
        scope.Dispose();
        using (var independent = Probe.BeginStyleEvaluation("fresh", 1, this, this, null, true)) independent.Returned(5, null, false);
        var row = Assert.Single(new StyleEvaluationIndex(View(fresh.Drain())).Calls);
        Assert.Equal("fresh", row.Name); Assert.Equal(0, row.ParentEvaluationId); Assert.Equal(0, fresh.ProbeFaults);
    }
    [Fact] public void WarmEnabledAndPausedEvaluationsAllocateNoManagedBytes()
    {
        using var session = Probe.Start(new() { TrackStyleEvaluations = true, EventsPerThread = 65536 }); var activator = new object();
        Action<int> call = count => { for (var i = 0; i < count; i++) { using var s = Probe.BeginStyleEvaluation("hot", 1, this, this, this, true); s.Returned(3, activator, false); } };
        var bytes = AllocationMeasurement.Measure(call); Assert.Equal(0, bytes);
        session.SetRecording(false); bytes = AllocationMeasurement.Measure(call); Assert.Equal(0, bytes);
        Assert.Equal(0, session.ProbeFaults); Assert.Equal(0, session.DroppedEvents);
    }
    [Fact] public async Task RewrittenFrameworkPreservesSelectorsActivationsOriginalExceptionsAndExports()
    {
        var original = StylingSubject.Exercise();
        var directory = Path.Combine(Path.GetTempPath(), "styling-weave-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var fixture = typeof(StylingSubject).Assembly.Location;
        foreach (var file in new[] { fixture, typeof(Avalonia.Styling.Selector).Assembly.Location, typeof(Avalonia.Controls.Border).Assembly.Location })
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        var context = new Context(directory);
        try
        {
            var manifest = WeaveEngine.Weave(new(directory) { Include = [Path.GetFileName(fixture)], Bootstrap = false, AllowSignedRewrite = true });
            var adapters = manifest.Assemblies.SelectMany(a => a.Methods).Where(m => m.Status == "StyleEvaluation").ToArray();
            Assert.True(adapters.Length >= 10, string.Join("\n", manifest.Assemblies.SelectMany(a => a.Methods).Where(m => m.Signature.Contains("Styling")).Select(m => m.ToString())));
            Assert.Contains(adapters, m => m.Signature.Contains("GetIsActive")); Assert.Contains(adapters, m => m.Signature.Contains("OnNext"));
            using var capture = Probe.Start(new() { TrackStyleEvaluations = true, EventsPerThread = 65536 });
            var type = context.LoadFromAssemblyPath(Path.Combine(directory, Path.GetFileName(fixture))).GetType(typeof(StylingSubject).FullName!)!;
            Assert.Equal(original, (string)type.GetMethod(nameof(StylingSubject.Exercise))!.Invoke(null, null)!);
            var trace = View(capture.Drain()); var index = new StyleEvaluationIndex(trace);
            foreach (var status in new[] { "Always this type", "Never this type", "Never this instance", "Conditional (activator)", "Always this instance", "Active", "Inactive", "Signal returned", "Threw" })
                Assert.Contains(index.Calls, r => r.Status == status);
            Assert.Contains(index.Calls, r => r.Kind == StyleEvaluationKind.Frame && r.Changed == true);
            Assert.Equal(0, index.InvalidMarkers); Assert.Equal(0, capture.ProbeFaults);
            using var stream = new MemoryStream(); await TraceExport.WriteBinaryAsync(stream, trace); stream.Position = 0;
            var store = new TraceStore(); await TraceFile.ReadAsync(stream, b => { store.Add(b); return ValueTask.CompletedTask; });
            Assert.Equal(index.Calls, new StyleEvaluationIndex(store.GetView()).Calls);
            using var chrome = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(chrome, trace);
            Assert.Contains("styleOutcome", System.Text.Encoding.UTF8.GetString(chrome.ToArray()));
        }
        finally { context.Unload(); Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrecognizedResultEnumsAndNontrivialSourceGettersAreNotSemanticallyRewritten(bool sourceGetter)
    {
        var directory = Path.Combine(Path.GetTempPath(), "styling-shape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Avalonia.Base.dll");
        try
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(Avalonia.Styling.Selector).Assembly.Location))
            {
                if (sourceGetter)
                {
                    var method = assembly.MainModule.GetType("Avalonia.Styling.StyleInstance").Methods.Single(m => m.Name == "get_Source");
                    var il = method.Body.GetILProcessor(); var first = method.Body.Instructions[0];
                    il.InsertBefore(first, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_0));
                    il.InsertBefore(first, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Pop));
                }
                else assembly.MainModule.GetType("Avalonia.Styling.SelectorMatchResult").Fields.Single(f => f.Name == "Sometimes").Constant = 42;
                assembly.Write(file);
            }
            var manifest = WeaveEngine.Weave(new(directory) { Bootstrap = false, AllowSignedRewrite = true });
            var reports = manifest.Assemblies.SelectMany(a => a.Methods).ToArray();
            var skipped = reports.Where(r => r.Status == "Skipped" && r.Note?.Contains(sourceGetter ? "frame fields" : "result enum", StringComparison.Ordinal) == true).ToArray();
            Assert.NotEmpty(skipped);
            if (sourceGetter)
            {
                Assert.Contains(skipped, r => r.Signature.Contains("GetIsActive"));
                Assert.Contains(skipped, r => r.Signature.Contains("OnNext"));
                Assert.Contains(reports, r => r.Status == "StyleEvaluation" && r.Signature.Contains("Match"));
            }
            else Assert.DoesNotContain(reports, r => r.Status == "StyleEvaluation");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static TraceView View(TraceBlock block) { var store = new TraceStore(); store.Add(block); return store.GetView(); }
    private sealed class Context(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(Probe).Assembly.GetName().Name) return typeof(Probe).Assembly;
            if (name.Name == typeof(Subsystem).Assembly.GetName().Name) return typeof(Subsystem).Assembly;
            var path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
