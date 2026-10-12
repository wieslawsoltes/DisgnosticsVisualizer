using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Observes existing invocations/results; never replays conversion, alters subscriptions or substitutes a sink.</summary>
internal static class BindingOperationRewriter
{
    private const string CoreName = "Avalonia.Data.Core.UntypedBindingExpressionBase";
    private const string ExpressionName = "Avalonia.Data.Core.BindingExpression";
    private const string DynamicName = "Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExpression";
    private const string SinkName = "Avalonia.Data.Core.IBindingExpressionSink";
    private const string ErrorName = "Avalonia.Data.Core.BindingError";
    private sealed record Plan(MethodDefinition Method, BindingOperationKind Kind)
    {
        public int Expression { get; init; } = -1; // -1 denotes this; otherwise a parameter index.
        public int Target { get; init; } = -1;
        public int Property { get; init; } = -1;
        public int Participant { get; init; } = -1;
        public int Input { get; init; } = -1;
        public int Error { get; init; } = -1;
        public int Detail { get; init; } = -1;
        public int ValueChanged { get; init; } = -1;
        public int ErrorChanged { get; init; } = -1;
        public int Produce { get; init; } = -1;
        public bool Dynamic { get; init; }
        public bool Bridge { get; init; }
        public SourceSite? OriginalSite { get; init; }
    }
    private sealed record Shape(TypeDefinition Core, MethodDefinition Running, MethodDefinition Property,
        MethodDefinition ErrorType, FieldDefinition Unset, FieldDefinition Unchanged, FieldDefinition DoNothing, FieldDefinition Value);

    public static MethodReport[] Instrument(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name is not ("Avalonia.Base" or "Avalonia.Markup.Xaml")) return [];
        var plans = new List<Plan>(); var reports = new List<MethodReport>();
        var methods = Types(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        foreach (var m in methods)
        {
            var t = m.DeclaringType.FullName; Plan? p = null;
            if (t == CoreName)
            {
                if (m.Name == "AttachCore" && Signature(m, "System.Void", SinkName, "Avalonia.PropertyStore.ImmediateValueFrame", "Avalonia.AvaloniaObject", "Avalonia.AvaloniaProperty", "Avalonia.Data.BindingPriority"))
                    p = new(m, BindingOperationKind.Attach) { Target = 2, Property = 3, Participant = 0, Detail = 4 };
                if (m.Name == "Start" && Signature(m, "System.Void", "System.Boolean")) p = new(m, BindingOperationKind.Start) { Produce = 0 };
                if (m.Name == "Stop" && Signature(m, "System.Void")) p = new(m, BindingOperationKind.Stop);
                if (m.Name == "Dispose" && Signature(m, "System.Void")) p = new(m, BindingOperationKind.Dispose);
                if (m.Name == "PublishValue" && Signature(m, "System.Void", "System.Object", ErrorName)) p = new(m, BindingOperationKind.Publish) { Input = 0, Error = 1 };
                if (m.Name == "Convert" && Signature(m, "System.Object", "Avalonia.Data.Converters.IValueConverter", "System.Globalization.CultureInfo", "System.Object", "System.Object", "System.Type", ErrorName + "&") && !m.Parameters[5].IsOut)
                    p = new(m, BindingOperationKind.Convert) { Participant = 0, Input = 3, Error = 5 };
                if (m.Name == "ConvertBack" && Signature(m, "System.Object", "Avalonia.Data.Converters.IValueConverter", "System.Globalization.CultureInfo", "System.Object", "System.Object", "System.Type"))
                    p = new(m, BindingOperationKind.ConvertBack) { Participant = 0, Input = 3 };
            }
            else if (t == ExpressionName)
            {
                if (m.Name == "WriteValueToSource" && Signature(m, "System.Boolean", "System.Object")) p = new(m, BindingOperationKind.WriteSource) { Input = 0 };
                if (m.Name == "UpdateSource" && Signature(m, "System.Void")) p = new(m, BindingOperationKind.UpdateSource);
                if (m.Name == "UpdateTarget" && Signature(m, "System.Void")) p = new(m, BindingOperationKind.UpdateTarget);
                if (m.Name == "OnNodeValueChanged" && Signature(m, "System.Void", "System.Int32", "System.Object", "System.Exception")) p = new(m, BindingOperationKind.NodeValue) { Detail = 0, Input = 1, Error = 2 };
                if (m.Name == "OnNodeError" && Signature(m, "System.Void", "System.Int32", "System.String")) p = new(m, BindingOperationKind.NodeError) { Detail = 0 };
                if (m.Name == "OnDataValidationError" && Signature(m, "System.Void", "System.Exception")) p = new(m, BindingOperationKind.ValidationError);
            }
            else if (t == DynamicName)
            {
                if (m.Name == "Subscribe" && Signature(m, "System.Void", "Avalonia.Controls.IResourceHost")) p = new(m, BindingOperationKind.ResourceSubscribe) { Input = 0, Dynamic = true };
                if (m.Name == "Unsubscribe" && Signature(m, "System.Void", "Avalonia.Controls.IResourceHost")) p = new(m, BindingOperationKind.ResourceUnsubscribe) { Input = 0, Dynamic = true };
                if (m.Name == "ResourcesChanged" && Signature(m, "System.Void", "System.Object", "Avalonia.Controls.ResourcesChangedEventArgs")) p = new(m, BindingOperationKind.ResourceInvalidated) { Dynamic = true };
                if (m.Name == "ResourcesChanged2" && Signature(m, "System.Void", "System.Object", "Avalonia.Controls.ResourcesChangedToken")) p = new(m, BindingOperationKind.ResourceInvalidated) { Dynamic = true };
                if (m.Name == "OnResourceProviderOwnerChanged" && Signature(m, "System.Void", "System.Object", "System.EventArgs")) p = new(m, BindingOperationKind.OwnerChanged) { Dynamic = true };
                if (m.Name == "ActualThemeVariantChanged" && Signature(m, "System.Void", "System.Object", "System.EventArgs")) p = new(m, BindingOperationKind.ThemeChanged) { Dynamic = true };
                if (m.Name == "PublishValue" && Signature(m, "System.Void")) p = new(m, BindingOperationKind.ResolveResource) { Dynamic = true };
            }
            if (p is not null) plans.Add(p with { OriginalSite = SourceSiteBuilder.Create(module, m) });
        }
        if (plans.Count == 0) return [];
        var shape = ReadShape(module);
        if (shape is null) return plans.Select(p => Report(p, "Skipped", "Binding fields, sentinel identities or inert accessor contract is unsupported")).ToArray();
        var sink = shape.Core.Module.GetType(SinkName);
        var changed = sink?.Methods.SingleOrDefault(m => m.Name == "OnChanged" && Signature(m, "System.Void", CoreName, "System.Boolean", "System.Boolean", "System.Object", ErrorName));
        var completed = sink?.Methods.SingleOrDefault(m => m.Name == "OnCompleted" && Signature(m, "System.Void", CoreName));
        if (module.Assembly.Name.Name == "Avalonia.Base" && changed is not null && completed is not null)
        {
            // A bridge per original call site keeps source attribution correct, including queued closure callbacks.
            foreach (var method in methods)
            foreach (var call in method.Body.Instructions.ToArray())
            {
                if (call.OpCode != OpCodes.Callvirt || call.Previous?.OpCode.OpCodeType == OpCodeType.Prefix ||
                    call.Operand is not MethodReference reference || reference.DeclaringType.FullName != SinkName ||
                    reference.FullName != changed.FullName && reference.FullName != completed.FullName) continue;
                var bridge = new MethodDefinition($"__DiagnosticsBinding_{reference.Name}_{method.MetadataToken.ToInt32():X8}_{call.Offset:X4}",
                    MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
                bridge.Parameters.Add(new("sink", ParameterAttributes.None, module.ImportReference(reference.DeclaringType)));
                foreach (var parameter in reference.Parameters) bridge.Parameters.Add(new(parameter.Name, ParameterAttributes.None, module.ImportReference(parameter.ParameterType)));
                var il = bridge.Body.GetILProcessor();
                foreach (var parameter in bridge.Parameters) il.Append(Instruction.Create(OpCodes.Ldarg, parameter));
                il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(reference))); il.Append(Instruction.Create(OpCodes.Ret));
                shape.Core.Methods.Add(bridge);
                plans.Add(new(bridge, reference.Name == "OnChanged" ? BindingOperationKind.Deliver : BindingOperationKind.Complete)
                { Expression = 1, Participant = 0, Input = reference.Name == "OnChanged" ? 4 : -1, Error = reference.Name == "OnChanged" ? 5 : -1,
                  ValueChanged = reference.Name == "OnChanged" ? 2 : -1, ErrorChanged = reference.Name == "OnChanged" ? 3 : -1, Bridge = true,
                  OriginalSite = SourceSiteBuilder.Create(module, method) with { IlOffset = call.Offset } });
                call.OpCode = OpCodes.Call; call.Operand = bridge;
            }
        }
        foreach (var p in plans)
        {
            var reason = MethodRewriter.Unsupported(p.Method);
            if (p.Method.HasGenericParameters || p.Method.DeclaringType.HasGenericParameters || p.Method.IsStatic != p.Bridge) reason = "Unsupported generic or receiver shape";
            if (p.Dynamic && (Field(p.Method.DeclaringType, "_resourceKey", "System.Object", false) is null || Field(p.Method.DeclaringType, "_host", "Avalonia.Controls.IResourceHost", false) is null)) reason = "Unsupported dynamic resource identity fields";
            if (reason is null) Wrap(module, p, shape);
            reports.Add(Report(p, reason is null ? "BindingOperation" : "Skipped", reason ?? "Original invocation/result and reference identities; no replay or subscription"));
        }
        return reports.ToArray();
    }
    private static MethodReport Report(Plan p, string status, string reason)
        => new(p.Method.MetadataToken.ToInt32(), p.Method.FullName, Subsystem.Bindings.ToString(), status, 0, reason, p.OriginalSite);
    private static Shape? ReadShape(ModuleDefinition module)
    {
        ModuleDefinition baseModule = module;
        if (module.Assembly.Name.Name != "Avalonia.Base")
        {
            var reference = module.AssemblyReferences.SingleOrDefault(a => a.Name == "Avalonia.Base"); if (reference is null) return null;
            try { baseModule = module.AssemblyResolver.Resolve(reference).MainModule; } catch (AssemblyResolutionException) { return null; }
        }
        var core = baseModule.GetType(CoreName); if (core is null) return null;
        var running = AutoGetter(core, "get_IsRunning", "System.Boolean"); var property = AutoGetter(core, "get_TargetProperty", "Avalonia.AvaloniaProperty");
        var error = AutoGetter(baseModule.GetType(ErrorName), "get_ErrorType", "Avalonia.Data.BindingErrorType");
        var unset = Field(baseModule.GetType("Avalonia.AvaloniaProperty"), "UnsetValue", "System.Object", true);
        var unchanged = Field(core, "UnchangedValue", "System.Object", true);
        var nothing = Field(baseModule.GetType("Avalonia.Data.BindingOperations"), "DoNothing", "System.Object", true);
        var value = Field(core, "_value", "System.Object", false); var errorEnum = baseModule.GetType("Avalonia.Data.BindingErrorType");
        string[] names = ["None", "Error", "DataValidationError"];
        if (errorEnum is not { IsEnum: true } || errorEnum.Fields.Count(f => f.HasConstant) != 3 ||
            !errorEnum.Fields.Any(f => f.Name == "value__" && f.FieldType.MetadataType == MetadataType.Int32) ||
            !names.Select((name, index) => errorEnum.Fields.SingleOrDefault(f => f.Name == name) is { HasConstant: true } f && Convert.ToInt32(f.Constant) == index).All(v => v)) return null;
        return running is null || property is null || error is null || unset is null || !unset.IsInitOnly || unchanged is null || !unchanged.IsInitOnly ||
            nothing is null || !nothing.IsInitOnly || value is null ? null : new(core, running, property, error, unset, unchanged, nothing, value);
    }
    private static bool Signature(MethodReference method, string result, params string[] parameters)
        => method.ReturnType.FullName == result && method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters);
    private static FieldDefinition? Field(TypeDefinition? type, string name, string fieldType, bool isStatic)
        => type?.Fields.SingleOrDefault(f => f.Name == name && f.FieldType.FullName == fieldType && f.IsStatic == isStatic);
    private static MethodDefinition? AutoGetter(TypeDefinition? type, string name, string result)
    {
        var method = type?.Methods.SingleOrDefault(m => m.Name == name && !m.IsStatic && (!m.IsVirtual || m.IsFinal) && !m.HasParameters && m.ReturnType.FullName == result);
        var body = method?.Body?.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        return body is { Length: 3 } && body[0].OpCode == OpCodes.Ldarg_0 && body[1].OpCode == OpCodes.Ldfld &&
            body[1].Operand is FieldDefinition field && field.DeclaringType == type && !field.IsStatic && field.FieldType.FullName == result && body[2].OpCode == OpCodes.Ret ? method : null;
    }
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    { foreach (var type in types) { yield return type; foreach (var child in Types(type.NestedTypes)) yield return child; } }

    private static void Wrap(ModuleDefinition module, Plan plan, Shape shape)
    {
        var method = plan.Method; var body = method.Body; var il = body.GetILProcessor(); MethodRewriter.ExpandBranches(method);
        var first = body.Instructions[0]; var scope = new VariableDefinition(module.ImportReference(typeof(BindingOperationScope))); body.Variables.Add(scope);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void) { result = new(method.ReturnType); body.Variables.Add(result); }
        var report = Instruction.Create(OpCodes.Ldloca, scope);
        var finish = result is null ? Instruction.Create(OpCodes.Ret) : Instruction.Create(OpCodes.Ldloc, result);
        foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            if (result is null) { ret.OpCode = OpCodes.Leave; ret.Operand = report; }
            else { ret.OpCode = OpCodes.Stloc; ret.Operand = result; il.InsertAfter(ret, Instruction.Create(OpCodes.Leave, report)); }
        }
        foreach (var handler in body.ExceptionHandlers) { handler.TryEnd ??= report; handler.HandlerEnd ??= report; }
        var returnBody = new List<Instruction> { report, Instruction.Create(OpCodes.Call, module.ImportReference(typeof(BindingOperationScope).GetProperty(nameof(BindingOperationScope.IsRecorded))!.GetMethod!)) };
        var normalExit = Instruction.Create(OpCodes.Leave, finish);
        returnBody.Add(Instruction.Create(OpCodes.Brfalse, normalExit)); returnBody.Add(Instruction.Create(OpCodes.Ldloca, scope));
        if (plan.Kind == BindingOperationKind.WriteSource)
        { returnBody.Add(Instruction.Create(OpCodes.Ldc_I4_3)); returnBody.Add(Instruction.Create(OpCodes.Ldloc, result!)); returnBody.Add(Instruction.Create(OpCodes.Sub)); }
        else returnBody.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        LoadValue(returnBody, true); Facts(returnBody, true);
        returnBody.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(BindingOperationScope).GetMethod(nameof(BindingOperationScope.Returned))!)));
        returnBody.Add(normalExit); foreach (var instruction in returnBody) il.Append(instruction);
        var cleanup = Instruction.Create(OpCodes.Ldloca, scope); il.Append(cleanup);
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(BindingOperationScope).GetMethod(nameof(BindingOperationScope.Dispose))!)));
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(finish); if (result is not null) il.Append(Instruction.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = finish });
        var site = SourceSiteSymbol.Encode(plan.OriginalSite!); var prefix = new List<Instruction>(); var capture = Instruction.Create(OpCodes.Ldstr, site);
        prefix.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetProperty(nameof(Probe.BindingOperationsEnabled))!.GetMethod!)));
        prefix.Add(Instruction.Create(OpCodes.Brtrue, capture)); prefix.Add(Instruction.Create(OpCodes.Ldstr, site)); prefix.Add(Instruction.Create(OpCodes.Ldc_I4, (int)plan.Kind)); Expr(prefix);
        for (var i = 0; i < 4; i++) prefix.Add(Instruction.Create(OpCodes.Ldnull));
        prefix.Add(Instruction.Create(OpCodes.Ldc_I4_0)); prefix.Add(Instruction.Create(OpCodes.Ldc_I8, long.MinValue));
        prefix.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.BeginBindingOperation))!)));
        prefix.Add(Instruction.Create(OpCodes.Stloc, scope)); prefix.Add(Instruction.Create(OpCodes.Br, first));
        prefix.Add(capture); prefix.Add(Instruction.Create(OpCodes.Ldc_I4, (int)plan.Kind)); Expr(prefix); Arg(prefix, plan.Target);
        if (plan.Property >= 0) Arg(prefix, plan.Property); else Getter(prefix, shape.Property, false);
        if (plan.Dynamic) OwnField(prefix, "_resourceKey"); else Arg(prefix, plan.Participant);
        LoadValue(prefix, false); Facts(prefix, false);
        if (plan.Detail < 0) prefix.Add(Instruction.Create(OpCodes.Ldc_I8, long.MinValue)); else { Arg(prefix, plan.Detail); prefix.Add(Instruction.Create(OpCodes.Conv_I8)); }
        prefix.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.BeginBindingOperation))!)));
        prefix.Add(Instruction.Create(OpCodes.Stloc, scope)); foreach (var instruction in prefix) il.InsertBefore(first, instruction);
        body.InitLocals = true; body.MaxStackSize = Math.Max(body.MaxStackSize, 32);

        void Expr(List<Instruction> output) { if (plan.Expression < 0) output.Add(Instruction.Create(OpCodes.Ldarg_0)); else Arg(output, plan.Expression); }
        void Arg(List<Instruction> output, int index) { output.Add(index < 0 ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldarg, method.Parameters[index])); }
        void OwnField(List<Instruction> output, string name)
        { output.Add(Instruction.Create(OpCodes.Ldarg_0)); output.Add(Instruction.Create(OpCodes.Ldfld, method.DeclaringType.Fields.Single(f => f.Name == name))); }
        void Getter(List<Instruction> output, MethodReference getter, bool integer)
        {
            Expr(output); var nonnull = Instruction.Create(OpCodes.Call, module.ImportReference(getter)); var done = Instruction.Create(OpCodes.Nop);
            output.Add(Instruction.Create(OpCodes.Dup)); output.Add(Instruction.Create(OpCodes.Brtrue, nonnull)); output.Add(Instruction.Create(OpCodes.Pop));
            output.Add(integer ? Instruction.Create(OpCodes.Ldc_I4_M1) : Instruction.Create(OpCodes.Ldnull)); output.Add(Instruction.Create(OpCodes.Br, done)); output.Add(nonnull); output.Add(done);
        }
        bool HasValue(bool after) => after ? method.ReturnType.MetadataType == MetadataType.Object || plan.Kind is BindingOperationKind.Publish or BindingOperationKind.Deliver || plan.Dynamic : plan.Input >= 0 || plan.Dynamic;
        void LoadValue(List<Instruction> output, bool after)
        {
            if (after && method.ReturnType.MetadataType == MetadataType.Object) output.Add(Instruction.Create(OpCodes.Ldloc, result!));
            else if (after && plan.Kind == BindingOperationKind.Publish) { Expr(output); output.Add(Instruction.Create(OpCodes.Ldfld, module.ImportReference(shape.Value))); }
            else if (plan.Dynamic && (after || plan.Input < 0)) OwnField(output, "_host");
            else if (HasValue(after)) Arg(output, plan.Input); else output.Add(Instruction.Create(OpCodes.Ldnull));
        }
        void Facts(List<Instruction> output, bool after)
        {
            LoadValue(output, after); output.Add(Instruction.Create(OpCodes.Ldc_I4, HasValue(after) ? 1 : 0));
            output.Add(Instruction.Create(OpCodes.Ldsfld, module.ImportReference(shape.Unset)));
            output.Add(Instruction.Create(OpCodes.Ldsfld, module.ImportReference(shape.Unchanged)));
            output.Add(Instruction.Create(OpCodes.Ldsfld, module.ImportReference(shape.DoNothing)));
            Getter(output, shape.Running, true); Error(output); IntArg(output, plan.ValueChanged); IntArg(output, plan.ErrorChanged); IntArg(output, plan.Produce);
            output.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.BindingFacts))!)));
        }
        void IntArg(List<Instruction> output, int index) { if (index < 0) output.Add(Instruction.Create(OpCodes.Ldc_I4_M1)); else Arg(output, index); }
        void Error(List<Instruction> output)
        {
            if (plan.Kind is BindingOperationKind.NodeError or BindingOperationKind.ValidationError)
            { output.Add(Instruction.Create(OpCodes.Ldc_I4, plan.Kind == BindingOperationKind.NodeError ? 1 : 2)); return; }
            if (plan.Error < 0) { output.Add(Instruction.Create(OpCodes.Ldc_I4_M1)); return; }
            Arg(output, plan.Error); var errorParameter = method.Parameters[plan.Error].ParameterType;
            if (errorParameter is ByReferenceType) output.Add(Instruction.Create(OpCodes.Ldind_Ref));
            var nonnull = Instruction.Create(OpCodes.Nop); var done = Instruction.Create(OpCodes.Nop);
            output.Add(Instruction.Create(OpCodes.Dup)); output.Add(Instruction.Create(OpCodes.Brtrue, nonnull)); output.Add(Instruction.Create(OpCodes.Pop)); output.Add(Instruction.Create(OpCodes.Ldc_I4_0)); output.Add(Instruction.Create(OpCodes.Br, done)); output.Add(nonnull);
            if (errorParameter.FullName == "System.Exception") { output.Add(Instruction.Create(OpCodes.Pop)); output.Add(Instruction.Create(OpCodes.Ldc_I4_2)); }
            else output.Add(Instruction.Create(OpCodes.Call, module.ImportReference(shape.ErrorType)));
            output.Add(done);
        }
    }
}
