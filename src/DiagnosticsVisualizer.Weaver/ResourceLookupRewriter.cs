using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Observes original bool/out resource contracts; never repeats a lookup or materializes a candidate.</summary>
internal static class ResourceLookupRewriter
{
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "Avalonia.Controls.ResourceDictionary", "Avalonia.Controls.ResourceNodeExtensions", "Avalonia.StyledElement",
        "Avalonia.Application", "Avalonia.Styling.StyleBase", "Avalonia.Styling.Styles",
        "Avalonia.Markup.Xaml.Styling.ResourceInclude", "Avalonia.Themes.Fluent.FluentTheme", "Avalonia.Themes.Simple.SimpleTheme"
    };
    public static MethodReport[] Instrument(ModuleDefinition module)
    {
        if (!module.Assembly.Name.Name.StartsWith("Avalonia", StringComparison.Ordinal)) return [];
        var reports = new List<MethodReport>();
        foreach (var type in module.Types.Where(t => Types.Contains(t.FullName)))
        foreach (var method in type.Methods.ToArray())
        {
            var name = method.Name.Split('.').Last();
            if (name is not ("TryGetResource" or "TryFindResource") && !(name == "TryGetValue" && type.Name == "ResourceDictionary")) continue;
            if (!method.HasBody) continue;
            var offset = method.IsStatic ? 1 : 0;
            var reason = MethodRewriter.Unsupported(method);
            var parameters = method.Parameters;
            if (method.HasGenericParameters || type.HasGenericParameters || method.ReturnType.MetadataType != MetadataType.Boolean ||
                parameters.Count - offset is not (2 or 3) ||
                (offset == 1 && (type.Name != "ResourceNodeExtensions" || parameters[0].ParameterType.IsValueType)) ||
                parameters[offset].ParameterType.MetadataType != MetadataType.Object ||
                parameters[^1].ParameterType is not ByReferenceType { ElementType.MetadataType: MetadataType.Object }) reason = "unsupported bool/key/out-object resource shape";
            MethodReference? themeGetter = null;
            if (reason is null && parameters.Count - offset == 3)
            {
                var theme = parameters[offset + 1].ParameterType;
                try
                {
                    var definition = theme.Resolve();
                    var getter = definition?.Methods.SingleOrDefault(m => m.Name == "get_Key" && !m.HasParameters);
                    var instructions = getter?.Body?.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
                    if (definition is not { IsSealed: true } || definition.FullName != "Avalonia.Styling.ThemeVariant" ||
                        getter is not { IsStatic: false, ReturnType.MetadataType: MetadataType.Object } ||
                        instructions is not { Length: 3 } || instructions[0].OpCode != OpCodes.Ldarg_0 ||
                        instructions[1].OpCode != OpCodes.Ldfld || instructions[2].OpCode != OpCodes.Ret)
                        reason = "theme key is not the known sealed auto-property shape";
                    else themeGetter = module.ImportReference(getter);
                }
                catch (AssemblyResolutionException) { reason = "theme type unavailable"; }
            }
            var source = SourceSiteBuilder.Create(module, method);
            if (reason is null) Wrap(module, method, offset, themeGetter, SourceSiteSymbol.Encode(source));
            reports.Add(new(method.MetadataToken.ToInt32(), method.FullName, "Resources", reason is null ? "ResourceLookup" : "Skipped", 0,
                reason ?? "Observed key/theme/provider and original bool/out result; exception unwinding preserved", source));
        }
        return reports.ToArray();
    }
    private static void Wrap(ModuleDefinition module, MethodDefinition method, int offset, MethodReference? themeGetter, string site)
    {
        var body = method.Body; var il = body.GetILProcessor(); MethodRewriter.ExpandBranches(method);
        var start = body.Instructions[0]; var scope = new VariableDefinition(module.ImportReference(typeof(ResourceLookupScope)));
        var result = new VariableDefinition(module.TypeSystem.Boolean); body.Variables.Add(scope); body.Variables.Add(result);
        var reportResult = Instruction.Create(OpCodes.Ldloca, scope); var end = Instruction.Create(OpCodes.Ldloc, result);
        foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        { ret.OpCode = OpCodes.Stloc; ret.Operand = result; il.InsertAfter(ret, Instruction.Create(OpCodes.Leave, reportResult)); }
        // The normal result report remains in the outer try. Do not dereference an unspecified out slot on false/throw.
        foreach (var handler in body.ExceptionHandlers) { handler.TryEnd ??= reportResult; handler.HandlerEnd ??= reportResult; }
        il.Append(reportResult); il.Append(Instruction.Create(OpCodes.Ldloc, result)); il.Append(Instruction.Create(OpCodes.Ldloc, result));
        var missing = Instruction.Create(OpCodes.Ldnull); var returned = Instruction.Create(OpCodes.Call, module.ImportReference(typeof(ResourceLookupScope).GetMethod(nameof(ResourceLookupScope.Returned))!));
        il.Append(Instruction.Create(OpCodes.Brfalse, missing)); il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[^1]));
        il.Append(Instruction.Create(OpCodes.Ldind_Ref)); il.Append(Instruction.Create(OpCodes.Br, returned)); il.Append(missing); il.Append(returned);
        il.Append(Instruction.Create(OpCodes.Leave, end)); var cleanup = Instruction.Create(OpCodes.Ldloca, scope); il.Append(cleanup);
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(ResourceLookupScope).GetMethod(nameof(ResourceLookupScope.Dispose))!)));
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(end); il.Append(Instruction.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = start, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = end });
        var prefix = new List<Instruction> { Instruction.Create(OpCodes.Ldstr, site),
            method.IsStatic ? Instruction.Create(OpCodes.Ldarg, method.Parameters[0]) : Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg, method.Parameters[offset]) };
        if (themeGetter is null) prefix.Add(Instruction.Create(OpCodes.Ldnull));
        else
        {
            var getKey = Instruction.Create(OpCodes.Call, themeGetter); var ready = Instruction.Create(OpCodes.Nop);
            prefix.Add(Instruction.Create(OpCodes.Ldarg, method.Parameters[offset + 1])); prefix.Add(Instruction.Create(OpCodes.Dup));
            prefix.Add(Instruction.Create(OpCodes.Brtrue, getKey)); prefix.Add(Instruction.Create(OpCodes.Br, ready));
            prefix.Add(getKey); prefix.Add(ready);
        }
        prefix.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.BeginResourceLookup))!)));
        prefix.Add(Instruction.Create(OpCodes.Stloc, scope)); foreach (var instruction in prefix) il.InsertBefore(start, instruction);
        body.InitLocals = true; body.MaxStackSize = Math.Max(body.MaxStackSize, 8);
    }
}
