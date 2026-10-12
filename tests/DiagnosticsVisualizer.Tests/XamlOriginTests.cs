using System.Reflection;
using System.Runtime.Loader;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class XamlOriginTests
{
    private sealed class Context() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name == typeof(Probe).Assembly.GetName().Name ? typeof(Probe).Assembly :
            name.Name == typeof(Subsystem).Assembly.GetName().Name ? typeof(Subsystem).Assembly : null;
    }
    [Fact] public async Task RewrittenConstructionAndRootPreserveIdentityExceptionsAndExport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xaml-origins-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var source = typeof(DiagnosticsVisualizer.Weaver.Fixtures.XamlSubject).Assembly.Location;
        var target = Path.Combine(dir, Path.GetFileName(source)); var context = new Context();
        try
        {
            File.Copy(source, target); File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(target, ".pdb"));
            var report = WeaveEngine.Weave(new(dir) { Include = [Path.GetFileName(target)], Bootstrap = false, Deep = true });
            var sites = report.Assemblies.SelectMany(a => a.Methods).Where(m => m.Status == "XamlSource" && m.Source!.Document == "Widget.axaml").ToArray();
            Assert.Equal(2, sites.Length); Assert.All(sites, site => Assert.True(site.Source!.IlOffset >= 0));
            using var session = Probe.Start(new() { TrackXamlObjects = true });
            var type = context.LoadFromAssemblyPath(target).GetType("DiagnosticsVisualizer.Weaver.Fixtures.XamlSubject")!;
            var build = type.GetMethod("Build")!;
            var first = build.Invoke(null, [false])!; var second = build.Invoke(null, [false])!;
            Assert.NotSame(first, second);
            var failure = Assert.Throws<TargetInvocationException>(() => build.Invoke(null, [true]));
            Assert.Equal("Original constructor failure", failure.InnerException!.Message);
            type.GetMethod("__AvaloniaXamlIlPopulate")!.Invoke(null, [new object(), first]);
            Assert.Equal(17, first.GetType().GetField("Value")!.GetValue(first));
            var store = new TraceStore(); store.Add(session.Drain()); var trace = store.GetView();
            var index = new XamlSourceIndex(trace); Assert.Equal(3, index.Origins.Length);
            Assert.Equal(2, index.Origins.Select(o => o.ObjectId).Distinct().Count());
            var origin = index.Find(session.GetObjectId(first))!;
            Assert.Equal(SourceMappingKind.XamlRoot, origin.Site.Mapping); Assert.Equal(30, origin.Site.Line);
            Assert.All(index.Origins.Where(o => o.Site.Mapping == SourceMappingKind.XamlConstruction), o => Assert.Equal(12, o.Site.Line));
            using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, trace); output.Position = 0;
            var restored = new TraceStore(); await TraceFile.ReadAsync(output, b => { restored.Add(b); return ValueTask.CompletedTask; });
            Assert.Equal(index.Origins, new XamlSourceIndex(restored.GetView()).Origins); Assert.Equal(0, session.ProbeFaults);
        }
        finally { context.Unload(); Directory.Delete(dir, true); }
    }
    [Fact] public void DisabledOriginsStayAllocationFreeAndDoNotClaimMissingObjects()
    {
        using var session = Probe.Start(); var target = new object();
        var allocated = AllocationMeasurement.Measure(count =>
        { for (var i = 0; i < count; i++) Probe.XamlObject(target, "disabled", (int)SourceMappingKind.XamlConstruction); });
        Assert.Equal(0, allocated); var store = new TraceStore(); store.Add(session.Drain());
        Assert.Null(new XamlSourceIndex(store.GetView()).Find(42)); Assert.Empty(store.GetView().Events);
    }
}
