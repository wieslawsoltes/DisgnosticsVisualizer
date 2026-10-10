using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Weaver;
using Mono.Cecil;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class HookCatalogTests
{
    [Fact]
    public void ApplicationControlOverridesAreMatchedOnlyInDeepMode()
    {
        using var module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        var layoutable = new TypeDefinition("Avalonia.Layout", "Layoutable", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(layoutable);
        var custom = new TypeDefinition("MyApp.Controls", "Dashboard", TypeAttributes.Public | TypeAttributes.Class, layoutable);
        module.Types.Add(custom);
        foreach (var name in new[] { "MeasureOverride", "ArrangeOverride" })
        {
            var method = new MethodDefinition(name, MethodAttributes.Family | MethodAttributes.Virtual, module.TypeSystem.Void);
            custom.Methods.Add(method);
            Assert.Equal(Subsystem.Layout, HookCatalog.Match(method, true));
            Assert.Null(HookCatalog.Match(method, false));
        }
    }
    [Fact]
    public void UnrelatedMethodsAreNotMistakenForLayoutOverrides()
    {
        using var module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        var custom = new TypeDefinition("MyApp", "Unrelated", TypeAttributes.Public | TypeAttributes.Class);
        module.Types.Add(custom);
        var method = new MethodDefinition("MeasureOverride", MethodAttributes.Public | MethodAttributes.Virtual, module.TypeSystem.Void);
        custom.Methods.Add(method);
        Assert.Null(HookCatalog.Match(method, true));
    }
}
