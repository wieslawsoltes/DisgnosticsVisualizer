using System.Reflection;
using System.Runtime.Loader;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using DiagnosticsVisualizer.Weaver.Fixtures;
using Xunit;

namespace DiagnosticsVisualizer.Tests;
public sealed class PropertyWeaverTests
{
    [Fact] public void OriginalGenericPropertyBehaviorAndNotificationValuesArePreserved()
    {
        var baseline = PropertySubject.Exercise();
        var directory = Path.Combine(Path.GetTempPath(), "property-weave-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var fixture = typeof(PropertySubject).Assembly.Location;
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!, "Avalonia*.dll").Append(fixture)) File.Copy(f, Path.Combine(directory, Path.GetFileName(f)), true);
        var context = new Context(directory);
        try
        {
            var manifest = WeaveEngine.Weave(new(directory) { Include = [Path.GetFileName(fixture)], Bootstrap = false, AllowSignedRewrite = true });
            var adapters = manifest.Assemblies.SelectMany(a => a.Methods).Where(m => m.Status == "PropertyOperation").ToArray();
            Assert.True(adapters.Length >= 12, string.Join("\n", manifest.Assemblies.SelectMany(a => a.Methods).Where(m => m.Category == "Properties")));
            using var capture = Probe.Start(new() { TrackPropertyOperations = true, CapturePropertyScalars = true, EventsPerThread = 131072 });
            var type = context.LoadFromAssemblyPath(Path.Combine(directory, Path.GetFileName(fixture))).GetType(typeof(PropertySubject).FullName!)!;
            var observed = (string)type.GetMethod(nameof(PropertySubject.Exercise))!.Invoke(null, null)!; Assert.Equal(baseline, observed);
            var trace = PropertyOperationTests.Read(capture); var index = new PropertyOperationIndex(trace);
            Assert.Equal(0, index.InvalidMarkers);
            Assert.Contains(index.Calls, r => r.Kind == PropertyOperationKind.Set && r.NewValue.Bits == 40);
            Assert.Contains(index.Calls, r => r.Effective && r.NewValue.Bits == 10);
            Assert.Contains(index.Calls, r => r.BaseValue && r.NewValue.Bits == 8);
            Assert.Contains(index.Calls, r => r.Kind == PropertyOperationKind.DirectSetAndRaise && r.Status == "Unchanged");
            Assert.Contains(index.Calls, r => r.Kind == PropertyOperationKind.DirectSetAndRaise && r.Status == "Changed");
            Assert.Contains(index.Calls, r => (r.Flags & 2) != 0); Assert.Contains(index.Calls, r => (r.Flags & 4) != 0);
            Assert.Contains(index.Calls, r => r.Effective && r.Priority == 4); Assert.Contains(index.Calls, r => r.Status == "Threw");
            Assert.Contains(index.Calls, r => r.Kind == PropertyOperationKind.ValueEntry && r.ParticipantId > 0);
            Assert.Equal(0, capture.ProbeFaults); Assert.Equal(0, capture.DroppedEvents);
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
}
