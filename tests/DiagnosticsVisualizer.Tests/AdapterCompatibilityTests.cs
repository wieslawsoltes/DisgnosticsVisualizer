using Mono.Cecil;
using DiagnosticsVisualizer.Weaver;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class AdapterCompatibilityTests
{
    [Fact]
    public void UnsupportedDispatcherShapeLeavesAssemblyUnchangedAndReportsTheMissingAdapter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adapter-shape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Avalonia.FutureShape.dll");
            using (var assembly = AssemblyDefinition.CreateAssembly(new("Avalonia.FutureShape", new Version(1, 0)), "Avalonia.FutureShape", ModuleKind.Dll))
            {
                foreach (var name in new[] { "DispatcherOperation", "DispatcherPriority", "DispatcherPriorityQueue" })
                    assembly.MainModule.Types.Add(new("Avalonia.Threading", name, TypeAttributes.Public, assembly.MainModule.TypeSystem.Object));
                assembly.Write(path);
            }
            var before = File.ReadAllBytes(path);
            var manifest = WeaveEngine.Weave(new(directory) { Bootstrap = false, Deep = true });
            var result = Assert.Single(manifest.Assemblies); var skipped = Assert.Single(result.Methods);
            Assert.Equal("Skipped", skipped.Status); Assert.Contains("no lifecycle hooks", skipped.Note);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
