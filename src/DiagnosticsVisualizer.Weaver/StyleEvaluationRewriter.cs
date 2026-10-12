using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Reads original return values and known inert fields. Never calls Evaluate or GetIsActive a second time.</summary>
internal static class StyleEvaluationRewriter
{
    public static MethodReport[] Instrument(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name != "Avalonia.Base") return [];
        var selector = module.GetType("Avalonia.Styling.Selector");
        var match = module.GetType("Avalonia.Styling.SelectorMatch");
        var result = module.GetType("Avalonia.Styling.SelectorMatchResult");
        var resultGetter = AutoGetter(match, "get_Result", "Avalonia.Styling.SelectorMatchResult");
        var activatorGetter = AutoGetter(match, "get_Activator", "Avalonia.Styling.Activators.IStyleActivator");
        string[] outcomes = ["NeverThisType", "NeverThisInstance", "Sometimes", "AlwaysThisInstance", "AlwaysThisType"];
        var validEnum = result is { IsEnum: true } && result.Fields.Count(f => f.HasConstant) == outcomes.Length &&
            result.Fields.Any(f => f.Name == "value__" && f.FieldType.MetadataType == MetadataType.Int32) && outcomes.Select((name, n) =>
            result.Fields.SingleOrDefault(f => f.Name == name) is { HasConstant: true } f && Convert.ToInt32(f.Constant) == n).All(v => v);
        var reports = new List<MethodReport>();
        foreach (var type in module.Types)
        foreach (var method in type.Methods.ToArray())
        {
            StyleEvaluationKind? kind = null;
            if (method.HasBody && !method.IsStatic && DerivesFrom(type, selector))
            {
                if (type == selector && method.Name == "Match") kind = StyleEvaluationKind.Match;
                else if (method.Name == "Evaluate") kind = StyleEvaluationKind.Evaluate;
            }
            if (type.FullName == "Avalonia.Styling.Style" && method.Name == "TryAttach") kind = StyleEvaluationKind.Attach;
            if (type.FullName == "Avalonia.Styling.StyleInstance")
            {
                if (method.Name == "GetIsActive") kind = StyleEvaluationKind.Frame;
                if (method.Name == "Avalonia.Styling.Activators.IStyleActivatorSink.OnNext") kind = StyleEvaluationKind.Signal;
            }
            if (kind is not { } operation) continue;
            var reason = MethodRewriter.Unsupported(method);
            var parameters = method.Parameters;
            if (!validEnum || match is not { IsValueType: true } || resultGetter is null || activatorGetter is null)
                reason = "selector result enum or inert accessor shape is unsupported";
            if (method.IsStatic || method.HasGenericParameters || type.HasGenericParameters) reason = "unsupported generic/static styling shape";
            if (operation is StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate)
            {
                if (method.ReturnType.FullName != match?.FullName || parameters.Count != 3 ||
                    parameters[0].ParameterType.FullName != "Avalonia.StyledElement" || parameters[1].ParameterType.FullName != "Avalonia.Styling.IStyle" ||
                    parameters[2].ParameterType.MetadataType != MetadataType.Boolean) reason = "unsupported selector evaluation contract";
            }
            else if (operation == StyleEvaluationKind.Attach)
            {
                if (method.ReturnType.FullName != result?.FullName || parameters.Count != 3 || parameters[0].ParameterType.FullName != "Avalonia.StyledElement" ||
                    parameters[1].ParameterType.MetadataType != MetadataType.Object || parameters[2].ParameterType.FullName != "Avalonia.PropertyStore.FrameType") reason = "unsupported style attachment contract";
            }
            var sourceGetter = AutoGetter(type, "get_Source", "Avalonia.Styling.IStyle");
            var activatorField = type.Fields.SingleOrDefault(f => f.Name == "_activator" && !f.IsStatic && f.FieldType.FullName == "Avalonia.Styling.Activators.IStyleActivator");
            if (operation is StyleEvaluationKind.Frame or StyleEvaluationKind.Signal)
            {
                if (sourceGetter is null || activatorField is null || parameters.Count != 1 ||
                    (operation == StyleEvaluationKind.Frame ? method.ReturnType.MetadataType != MetadataType.Boolean ||
                        parameters[0].ParameterType is not ByReferenceType { ElementType.MetadataType: MetadataType.Boolean } :
                        method.ReturnType.MetadataType != MetadataType.Void || parameters[0].ParameterType.MetadataType != MetadataType.Boolean))
                    reason = "unsupported style frame fields or callback contract";
            }
            var site = SourceSiteBuilder.Create(module, method);
            if (reason is null) Wrap(module, method, operation, resultGetter!, activatorGetter!, sourceGetter, activatorField, SourceSiteSymbol.Encode(site));
            reports.Add(new(method.MetadataToken.ToInt32(), method.FullName, StyleEvaluationEvent.Category(operation).ToString(),
                reason is null ? "StyleEvaluation" : "Skipped", 0, reason ?? $"Observed {operation}; original result/activator retained, no replay or subscription", site));
        }
        return reports.ToArray();
    }
    private static bool DerivesFrom(TypeDefinition type, TypeDefinition? parent)
    {
        if (parent is null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(type.FullName))
        {
            if (type == parent) return true;
            if (type.BaseType is null || type.BaseType.Namespace != "Avalonia.Styling") return false;
            try { if (type.BaseType.Resolve() is not { } next) return false; type = next; }
            catch (AssemblyResolutionException) { return false; }
        }
        return false;
    }
    private static MethodDefinition? AutoGetter(TypeDefinition? type, string name, string returns)
    {
        var getter = type?.Methods.SingleOrDefault(m => m.Name == name && !m.IsStatic && (!m.IsVirtual || m.IsFinal) && !m.HasParameters && m.ReturnType.FullName == returns);
        var body = getter?.Body?.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        return body is { Length: 3 } && body[0].OpCode == OpCodes.Ldarg_0 && body[1].OpCode == OpCodes.Ldfld &&
            body[1].Operand is FieldDefinition { IsStatic: false } field && field.DeclaringType == type && field.FieldType.FullName == returns && body[2].OpCode == OpCodes.Ret ? getter : null;
    }
    private static void Wrap(ModuleDefinition module, MethodDefinition method, StyleEvaluationKind kind, MethodReference resultGetter,
        MethodReference activatorGetter, MethodReference? sourceGetter, FieldReference? activatorField, string site)
    {
        var body = method.Body; var il = body.GetILProcessor(); MethodRewriter.ExpandBranches(method);
        var first = body.Instructions[0]; var scope = new VariableDefinition(module.ImportReference(typeof(StyleEvaluationScope)));
        body.Variables.Add(scope);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void) { result = new(method.ReturnType); body.Variables.Add(result); }
        var report = Instruction.Create(OpCodes.Ldloca, scope);
        var end = result is null ? Instruction.Create(OpCodes.Ret) : Instruction.Create(OpCodes.Ldloc, result);
        foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            if (result is null) { ret.OpCode = OpCodes.Leave; ret.Operand = report; }
            else { ret.OpCode = OpCodes.Stloc; ret.Operand = result; il.InsertAfter(ret, Instruction.Create(OpCodes.Leave, report)); }
        }
        foreach (var handler in body.ExceptionHandlers) { handler.TryEnd ??= report; handler.HandlerEnd ??= report; }
        il.Append(report);
        if (kind is StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate)
        {
            il.Append(Instruction.Create(OpCodes.Ldloca, result!)); il.Append(Instruction.Create(OpCodes.Call, resultGetter));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1)); il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Ldloca, result!)); il.Append(Instruction.Create(OpCodes.Call, activatorGetter));
        }
        else if (kind == StyleEvaluationKind.Attach)
        {
            il.Append(Instruction.Create(OpCodes.Ldloc, result!)); il.Append(Instruction.Create(OpCodes.Ldc_I4_1)); il.Append(Instruction.Create(OpCodes.Add)); il.Append(Instruction.Create(OpCodes.Ldnull));
        }
        else
        {
            if (kind == StyleEvaluationKind.Frame)
            { il.Append(Instruction.Create(OpCodes.Ldloc, result!)); il.Append(Instruction.Create(OpCodes.Ldc_I4_6)); il.Append(Instruction.Create(OpCodes.Add)); }
            else il.Append(Instruction.Create(OpCodes.Ldc_I4, (int)StyleEvaluationOutcome.Delivered));
            il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Ldfld, activatorField!));
        }
        if (kind == StyleEvaluationKind.Frame)
        { il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[0])); il.Append(Instruction.Create(OpCodes.Ldind_U1)); }
        else il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(StyleEvaluationScope).GetMethod(nameof(StyleEvaluationScope.Returned))!)));
        il.Append(Instruction.Create(OpCodes.Leave, end));
        var cleanup = Instruction.Create(OpCodes.Ldloca, scope); il.Append(cleanup);
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(StyleEvaluationScope).GetMethod(nameof(StyleEvaluationScope.Dispose))!)));
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(end); if (result is not null) il.Append(Instruction.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = end });
        var prefix = new List<Instruction> { Instruction.Create(OpCodes.Ldstr, site), Instruction.Create(OpCodes.Ldc_I4, (int)kind), Instruction.Create(OpCodes.Ldarg_0) };
        if (kind is StyleEvaluationKind.Frame or StyleEvaluationKind.Signal)
        { prefix.Add(Instruction.Create(OpCodes.Ldnull)); prefix.Add(Instruction.Create(OpCodes.Ldarg_0)); prefix.Add(Instruction.Create(OpCodes.Call, sourceGetter!)); }
        else
        { prefix.Add(Instruction.Create(OpCodes.Ldarg, method.Parameters[0])); prefix.Add(Instruction.Create(OpCodes.Ldarg, method.Parameters[1])); }
        prefix.Add(kind is StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate ? Instruction.Create(OpCodes.Ldarg, method.Parameters[2]) :
            kind == StyleEvaluationKind.Signal ? Instruction.Create(OpCodes.Ldarg, method.Parameters[0]) : Instruction.Create(OpCodes.Ldc_I4_0));
        prefix.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.BeginStyleEvaluation))!)));
        prefix.Add(Instruction.Create(OpCodes.Stloc, scope)); foreach (var i in prefix) il.InsertBefore(first, i);
        body.InitLocals = true; body.MaxStackSize = Math.Max(body.MaxStackSize, 10);
    }
}
