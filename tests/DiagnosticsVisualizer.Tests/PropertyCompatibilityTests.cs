using DiagnosticsVisualizer.Weaver;
using Mono.Cecil;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class PropertyCompatibilityTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ChangedPriorityOrOptionalStorageCannotReceiveSemanticHooks(bool changeEnum)
    {
        var dir = Path.Combine(Path.GetTempPath(), "property-shape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var input = typeof(Avalonia.AvaloniaObject).Assembly.Location; var path = Path.Combine(dir, "Avalonia.Base.dll");
        try
        {
            using (var module = ModuleDefinition.ReadModule(input))
            {
                if (changeEnum) module.GetType("Avalonia.Data.BindingPriority").Fields.Single(f => f.Name == "Animation").Constant = -2;
                else
                {
                    var type = module.GetType("Avalonia.Data.Optional`1");
                    // Renaming storage preserves validity of the original assembly, but must reject our raw-field reader.
                    type.Fields.Single(f => f.Name == "<HasValue>k__BackingField").Name = "_changedStorage";
                }
                module.Write(path);
            }
            var manifest = WeaveEngine.Weave(new(dir) { Bootstrap = false, AllowSignedRewrite = true });
            Assert.DoesNotContain(manifest.Assemblies.SelectMany(a => a.Methods), m => m.Status == "PropertyOperation");
            Assert.Contains(manifest.Assemblies.SelectMany(a => a.Methods), m => m.Category == "Properties" && m.Note?.StartsWith("Unsupported property notification") == true);
            using var output = ModuleDefinition.ReadModule(path);
            Assert.DoesNotContain(output.Types.SelectMany(t => t.Methods), m => m.Name == "__AvTracePropertyValue");
        }
        finally { Directory.Delete(dir, true); }
    }
}
