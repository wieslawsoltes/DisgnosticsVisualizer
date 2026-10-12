using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using Mono.Cecil;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class WeaverUpgradeTests
{
    [Fact] public void OlderHookOutputIsRejectedWithoutOverwritingOriginalBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "weave-upgrade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Fixture.dll");
        try
        {
            using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture", new Version(1, 0)), "Fixture", ModuleKind.Dll))
            {
                var module = assembly.MainModule;
                var marker = new CustomAttribute(module.ImportReference(typeof(InstrumentedAttribute).GetConstructor([typeof(string)])!));
                marker.ConstructorArguments.Add(new(module.TypeSystem.String, "obsolete")); assembly.CustomAttributes.Add(marker); assembly.Write(path);
            }
            var original = File.ReadAllBytes(path);
            var error = Assert.Throws<InvalidOperationException>(() => WeaveEngine.Weave(new(directory) { Include = ["Fixture.dll"], Bootstrap = false }));
            Assert.Contains("Restore or clean", error.Message); Assert.Equal(original, File.ReadAllBytes(path)); Assert.False(File.Exists(Path.Combine(directory, "avtrace.manifest.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
