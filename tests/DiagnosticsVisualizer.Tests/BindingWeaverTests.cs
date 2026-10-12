using System.Reflection;
using System.Runtime.Loader;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using DiagnosticsVisualizer.Weaver.Fixtures;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class BindingWeaverTests
{
    [Fact] public void RewrittenFrameworkPreservesBindingConverterAndResourceBehavior()
    {
        var directory = Path.Combine(Path.GetTempPath(), "binding-weave-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var fixture = typeof(BindingSubject).Assembly.Location; var originalDirectory = Path.GetDirectoryName(fixture)!;
        foreach (var file in Directory.EnumerateFiles(originalDirectory, "Avalonia*.dll").Append(fixture)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        var context = new Context(directory);
        try
        {
            var manifest = WeaveEngine.Weave(new(directory) { Include = [Path.GetFileName(fixture)], Bootstrap = false, AllowSignedRewrite = true });
            var adapters = manifest.Assemblies.SelectMany(a => a.Methods).Where(m => m.Status == "BindingOperation").ToArray();
            Assert.True(adapters.Length >= 20, string.Join("\n", manifest.Assemblies.SelectMany(a => a.Methods).Where(m => m.Signature.Contains("Binding") || m.Signature.Contains("DynamicResource"))));
            var type = context.LoadFromAssemblyPath(Path.Combine(directory, Path.GetFileName(fixture))).GetType(typeof(BindingSubject).FullName!)!;
            var method = type.GetMethod(nameof(BindingSubject.Exercise))!;
            // Same rewritten code without capture is the baseline; no different source fixture or converter is substituted.
            var baseline = (string)method.Invoke(null, null)!;
            using var capture = Probe.Start(new() { TrackBindingOperations = true, EventsPerThread = 131072 });
            var observed = (string)method.Invoke(null, null)!; Assert.Equal(baseline, observed);
            var store = new TraceStore();
            while (true) { var batch = capture.Drain(); store.Add(batch); if (batch.Events.Length == 0) break; }
            var index = new BindingOperationIndex(store.GetView());
            foreach (var kind in new[] { BindingOperationKind.Attach, BindingOperationKind.Start, BindingOperationKind.Stop, BindingOperationKind.Dispose,
                BindingOperationKind.Publish, BindingOperationKind.Deliver, BindingOperationKind.Complete, BindingOperationKind.Convert,
                BindingOperationKind.ConvertBack, BindingOperationKind.WriteSource, BindingOperationKind.NodeValue, BindingOperationKind.NodeError,
                BindingOperationKind.ResourceSubscribe, BindingOperationKind.ResourceUnsubscribe, BindingOperationKind.ResolveResource, BindingOperationKind.ResourceInvalidated })
                Assert.Contains(index.Calls, c => c.Kind == kind);
            Assert.Contains(index.Calls, c => c.InputKind == "Null"); Assert.Contains(index.Calls, c => c.OutputKind == "DoNothing");
            Assert.Contains(index.Calls, c => c.OutputKind == "Unset" && c.Error == "Binding error");
            Assert.Contains(index.Calls, c => c.Kind == BindingOperationKind.WriteSource && c.Outcome == BindingOperationOutcome.True);
            Assert.Equal(0, index.InvalidMarkers); Assert.Equal(0, capture.ProbeFaults); Assert.Equal(0, capture.DroppedEvents);
        }
        finally { context.Unload(); Directory.Delete(directory, true); }
    }
    [Fact] public void NontrivialStateAccessorRejectsSemanticRewriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "binding-shape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(Avalonia.AvaloniaObject).Assembly.Location))
            {
                var method = assembly.MainModule.GetType("Avalonia.Data.Core.UntypedBindingExpressionBase").Methods.Single(m => m.Name == "get_IsRunning");
                var il = method.Body.GetILProcessor(); var first = method.Body.Instructions[0];
                il.InsertBefore(first, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_0));
                il.InsertBefore(first, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Pop));
                assembly.Write(Path.Combine(directory, "Avalonia.Base.dll"));
            }
            var manifest = WeaveEngine.Weave(new(directory) { Bootstrap = false, AllowSignedRewrite = true });
            Assert.DoesNotContain(manifest.Assemblies.SelectMany(a => a.Methods), m => m.Status == "BindingOperation");
            Assert.Contains(manifest.Assemblies.SelectMany(a => a.Methods), m => m.Status == "Skipped" && m.Note?.Contains("inert accessor", StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(directory, true); }
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
