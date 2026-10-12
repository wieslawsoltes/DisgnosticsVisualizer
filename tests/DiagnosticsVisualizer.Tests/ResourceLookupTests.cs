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

public sealed class ResourceLookupTests
{
    [Fact] public void CapturesNestedResultsAndPhysicalCausalityWithoutCountingDescriptorsAsTime()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true }); var provider = new object(); var value = new object();
        using (var outer = Probe.BeginResourceLookup("outer", provider, "Accent", "Dark"))
        {
            using (var inner = Probe.BeginResourceLookup("inner", provider, "Accent", "Dark"))
            { Probe.Instant(Subsystem.Properties, "mutation", provider); inner.Returned(true, value); }
            outer.Returned(true, value);
        }
        var trace = View(session.Drain()); var index = new ResourceLookupIndex(trace); var root = Assert.Single(index.Roots);
        Assert.Equal(2, index.Calls.Length); Assert.Equal("Found", root.Status); Assert.Equal("Accent", root.Key); Assert.Equal("Dark", root.Theme);
        var child = Assert.Single(index.Calls, c => c.ParentLookupId == root.Id);
        Assert.Equal(child.Id, Assert.Single(trace.Events, e => trace.Name(e) == "mutation").ParentId);
        Assert.Equal(root.ValueId, child.ValueId); Assert.Equal(2, index.Descendants(root.Id).Length);
        Assert.True(TraceAnalyzer.Analyze(trace).Costs.Sum(c => c.SelfMs) <= root.DurationMs + .001);
        Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void SamplesWholeTreesAndPausedCaptureCompletesAdmittedCalls()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true, SampleEvery = 2 });
        using (var ignored = Probe.BeginResourceLookup("ignored", this, "K", null))
        { using var child = Probe.BeginResourceLookup("also ignored", this, "K", null); child.Returned(false, null); ignored.Returned(false, null); }
        using (var root = Probe.BeginResourceLookup("root", this, "K", null))
        { session.SetRecording(false); using var child = Probe.BeginResourceLookup("child", this, "K", null); child.Returned(true, null); root.Returned(true, null); }
        var index = new ResourceLookupIndex(View(session.Drain())); Assert.Equal(2, index.Calls.Length); Assert.Equal("root", Assert.Single(index.Roots).Name);
        Assert.All(index.Calls, c => Assert.Equal("Found null", c.Status)); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void WarmLookupsAndDisabledLookupsRemainAllocationFree()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true, EventsPerThread = 65536 }); var value = new object();
        void Call() { using var scope = Probe.BeginResourceLookup("hot", this, "K", "Dark"); scope.Returned(true, value); }
        for (var i = 0; i < 256; i++) Call();
        var start = GC.GetAllocatedBytesForCurrentThread(); for (var i = 0; i < 10000; i++) Call();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - start; Assert.Equal(0, bytes);
        session.SetRecording(false); start = GC.GetAllocatedBytesForCurrentThread(); for (var i = 0; i < 10000; i++) Call();
        bytes = GC.GetAllocatedBytesForCurrentThread() - start; Assert.Equal(0, bytes); Assert.Equal(0, session.DroppedEvents); Assert.Equal(0, session.ProbeFaults);
    }
    [Fact] public void IncompleteAndConflictingObservationsRemainUnknown()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true });
        using (var scope = Probe.BeginResourceLookup("fault", this, "Key", null)) { }
        var trace = View(session.Drain()); var row = Assert.Single(new ResourceLookupIndex(trace).Calls); Assert.Equal("Threw", row.Status);
        var partial = trace with { Events = trace.Events.Where(e => !ResourceLookupEvent.IsCompletion(e)).ToArray() };
        Assert.Null(Assert.Single(new ResourceLookupIndex(partial).Calls).DurationMs);
        Assert.True(Assert.Single(new ResourceLookupIndex(trace with { MissingBlocks = 1 }).Calls).CaptureIncomplete);
        var duplicate = row.Request!.Value with { Id = 98765 };
        Assert.Equal("Conflicting / invalid", Assert.Single(new ResourceLookupIndex(trace with { Events = [.. trace.Events, duplicate] }).Calls).Status);
    }
    [Fact] public async Task ActualRewrittenAvaloniaPreservesMergedThemeDeferredNullAndExceptionBehavior()
    {
        var directory = Path.Combine(Path.GetTempPath(), "resource-weave-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var fixture = typeof(ResourceSubject).Assembly.Location;
        foreach (var file in new[] { fixture, typeof(Avalonia.Controls.ResourceDictionary).Assembly.Location }) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var context = new Context(directory);
        try
        {
            var manifest = WeaveEngine.Weave(new(directory) { Include = [Path.GetFileName(fixture)], Bootstrap = false, AllowSignedRewrite = true });
            Assert.True(manifest.Assemblies.SelectMany(a => a.Methods).Count(m => m.Status == "ResourceLookup") >= 6);
            using var capture = Probe.Start(new() { TrackResourceLookups = true, EventsPerThread = 65536 });
            var type = context.LoadFromAssemblyPath(Path.Combine(directory, Path.GetFileName(fixture))).GetType(typeof(ResourceSubject).FullName!)!;
            Assert.Equal("RESOURCE_LOOKUP_OK merged=higher theme=dark lazy=1 null=found missing=false opaque=safe fault=preserved", type.GetMethod(nameof(ResourceSubject.Exercise))!.Invoke(null, null));
            var trace = View(capture.Drain()); var index = new ResourceLookupIndex(trace);
            Assert.Contains(index.Roots, r => r.Key == "Shared" && r.Status == "Found");
            Assert.Contains(index.Roots, r => r.Key == "Themed" && r.Theme == "Dark" && r.Status == "Found");
            Assert.Contains(index.Roots, r => r.Key == "Null" && r.Status == "Found null");
            Assert.Contains(index.Roots, r => r.Key == "Missing" && r.Status == "Missing");
            Assert.Contains(index.Roots, r => r.Key == "Throwing" && r.Status == "Threw");
            Assert.Contains(index.Roots, r => r.Key.Contains("opaque key", StringComparison.Ordinal));
            Assert.Equal(0, index.InvalidMarkers); Assert.Equal(0, capture.ProbeFaults);
            using var stream = new MemoryStream(); await TraceExport.WriteBinaryAsync(stream, trace); stream.Position = 0;
            var store = new TraceStore(); await TraceFile.ReadAsync(stream, b => { store.Add(b); return ValueTask.CompletedTask; });
            Assert.Equal(index.Calls, new ResourceLookupIndex(store.GetView()).Calls);
        }
        finally { context.Unload(); Directory.Delete(directory, true); }
    }
    [Fact] public void CyclicAndImpossibleNestingCannotInventValidLookupPaths()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true });
        using (var root = Probe.BeginResourceLookup("root", this, "Key", null))
        { using var child = Probe.BeginResourceLookup("child", this, "Key", null); child.Returned(false, null); root.Returned(false, null); }
        var trace = View(session.Drain()); var good = new ResourceLookupIndex(trace); var rootId = Assert.Single(good.Roots).Id;
        var childId = Assert.Single(good.Calls, r => r.ParentLookupId == rootId).Id;
        var cycle = new ResourceLookupIndex(trace with { Events = trace.Events.Select(e => ResourceLookupEvent.LookupId(e) == rootId ? e with { Data1 = childId } : e).ToArray() });
        Assert.Equal(2, cycle.InvalidMarkers); Assert.Equal(2, cycle.Roots.Length);
        Assert.All(cycle.Calls, r => { Assert.Null(r.DurationMs); Assert.True(r.CaptureIncomplete); Assert.NotNull(cycle.RootOf(r.Id)); });
        Assert.Equal(2, cycle.Descendants(rootId).Length); Assert.Single(cycle.Descendants(rootId, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => cycle.Descendants(rootId, 0));
        var impossible = new ResourceLookupIndex(trace with { Events = trace.Events.Select(e => ResourceLookupEvent.LookupId(e) == childId ? e with { ThreadId = e.ThreadId + 1 } : e).ToArray() });
        Assert.Equal("Conflicting / invalid", impossible.Find(childId)!.Status);
    }
    [Fact] public void CustomKeysAreWeakOpaqueAndNeverInvokeUserFormattingOrEquality()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true });
        var weak = ObserveOpaque();
        for (var attempt = 0; attempt < 5 && weak.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
        using (var scope = Probe.BeginResourceLookup("reserved", this, SourceSiteSymbol.Prefix + "not-json\nline", null)) scope.Returned(false, null);
        var trace = View(session.Drain()); Assert.Empty(trace.SourceSites);
        var rows = new ResourceLookupIndex(trace).Calls;
        Assert.Contains(rows, r => r.Key.Contains("opaque key", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Key.Contains("\\nline", StringComparison.Ordinal));
        Assert.Equal(0, session.ProbeFaults);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ObserveOpaque()
    {
        var key = new HostileKey(); var weak = new WeakReference(key);
        using var scope = Probe.BeginResourceLookup("opaque", null, key, key); scope.Returned(false, null); return weak;
    }
    private sealed class HostileKey
    {
        public override string ToString() => throw new InvalidOperationException("Formatting must not run");
        public override int GetHashCode() => throw new InvalidOperationException("Hashing must not run");
        public override bool Equals(object? obj) => throw new InvalidOperationException("Equality must not run");
    }
    [Fact] public void MetadataAndBufferBudgetsRemainVisibleRatherThanClaimingFullCoverage()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true, MaxObjects = 1, MaxSymbols = 3, EventsPerThread = 16 });
        for (var i = 0; i < 20; i++)
        { using var scope = Probe.BeginResourceLookup("lookup", this, new HostileKey(), null); scope.Returned(true, new object()); }
        var block = session.Drain(); Assert.True(block.DroppedEvents > 0); Assert.True(block.MetadataDrops > 0);
        Assert.Equal(0, block.ProbeFaults); Assert.All(new ResourceLookupIndex(View(block)).Calls, r => Assert.True(r.CaptureIncomplete));
    }
    [Fact] public void DisabledSemanticCapturePreservesOrdinaryResourceSpansAndNewSessionIsolation()
    {
        using (var session = Probe.Start())
        {
            using (var scope = Probe.BeginResourceLookup("fallback", this, "Key", null)) scope.Returned(false, null);
            var e = Assert.Single(session.Drain().Events); Assert.Equal(TraceEventKind.Span, e.Kind); Assert.False(ResourceLookupEvent.IsEvent(e));
        }
        var old = Probe.Start(new() { TrackResourceLookups = true, FlowExecutionContext = true });
        var pending = Probe.BeginResourceLookup("old", this, "Key", null); old.Dispose();
        using var replacement = Probe.Start(new() { TrackResourceLookups = true, FlowExecutionContext = true });
        pending.Returned(true, this); pending.Dispose();
        using (var scope = Probe.BeginResourceLookup("new", this, "Key", null)) scope.Returned(false, null);
        var root = Assert.Single(new ResourceLookupIndex(View(replacement.Drain())).Roots;
        Assert.Equal("new", root.Name); Assert.Equal(0, root.ParentLookupId); Assert.Equal(0, root.Completion!.Value.ParentId);
        Assert.Equal(0, replacement.ProbeFaults);
    }
    [Fact] public async Task ChromeExportRetainsObservedOutcomeAndIdentityWithoutResourceContents()
    {
        using var session = Probe.Start(new() { TrackResourceLookups = true });
        using (var scope = Probe.BeginResourceLookup("lookup", this, "Accent", "Dark")) scope.Returned(true, new HostileKey());
        var trace = View(session.Drain()); using var stream = new MemoryStream();
        await TraceExport.WriteChromeJsonAsync(stream, trace);
        using var json = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        var e = Assert.Single(json.RootElement.GetProperty("traceEvents").EnumerateArray(), e => e.GetProperty("ph").GetString() == "X");
        var args = e.GetProperty("args"); Assert.Equal("Found", args.GetProperty("resourceOutcome").GetString());
        Assert.Equal("Accent", args.GetProperty("resourceKey").GetString()); Assert.Equal("Dark", args.GetProperty("resourceTheme").GetString());
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
