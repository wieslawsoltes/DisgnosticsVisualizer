using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Observes original property APIs and notification arguments; never reads the current property or replays coercion.</summary>
internal static class PropertyOperationRewriter
{
    private sealed record Plan(MethodDefinition Method, PropertyOperationKind Kind, int Property = 0, int Input = -1, int Priority = -1,
        bool ArgsNotification = false, bool Entry = false, bool Setter = false);
    private static bool Auto(MethodDefinition? m) => m is { HasBody: true, IsStatic: false } && (!m.IsVirtual || m.IsFinal) &&
        m.Body.ExceptionHandlers.Count == 0 && m.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray() is { Length: 3 } code &&
        code[0].OpCode == OpCodes.Ldarg_0 && code[1].OpCode == OpCodes.Ldfld && code[2].OpCode == OpCodes.Ret &&
        code[1].Operand is FieldReference field && m.Name.StartsWith("get_", StringComparison.Ordinal) &&
        field.Name == $"<{m.Name[4..]}>k__BackingField" && field.FieldType.FullName == m.ReturnType.FullName;
    public static MethodReport[] Instrument(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name != "Avalonia.Base") return [];
        var owner = module.GetType("Avalonia.AvaloniaObject"); var property = module.GetType("Avalonia.AvaloniaProperty");
        if (owner is null || property is null) return [];
        var getters = new[] { "get_Name", "get_IsDirect", "get_IsAttached", "get_Inherits" }.Select(n => property.Methods.SingleOrDefault(m => m.Name == n)).ToArray();
        var plans = new List<Plan>();
        foreach (var m in owner.Methods.ToArray())
        {
            var p = m.Parameters;
            if (m.Name is "SetValue" or "SetCurrentValue" && p.Count is 2 or 3 && IsProperty(p[0].ParameterType))
                plans.Add(new(m, m.Name == "SetValue" ? PropertyOperationKind.Set : PropertyOperationKind.SetCurrent, Input: 1, Priority: p.Count == 3 ? 2 : -1));
            else if (m.Name is "ClearValue" or "CoerceValue" && p.Count == 1 && IsProperty(p[0].ParameterType))
                plans.Add(new(m, m.Name == "ClearValue" ? PropertyOperationKind.Clear : PropertyOperationKind.Coerce));
            else if (m.Name == "SetAndRaise" && p.Count == 3 && IsProperty(p[0].ParameterType) && p[1].ParameterType.IsByReference && m.ReturnType.MetadataType == MetadataType.Boolean)
                plans.Add(new(m, PropertyOperationKind.DirectSetAndRaise, Input: 2));
            else if (m.Name == "RaisePropertyChanged" && p.Count == 5 && IsProperty(p[0].ParameterType) &&
                p[1].ParameterType.GetElementType().FullName == "Avalonia.Data.Optional`1" && p[2].ParameterType.GetElementType().FullName == "Avalonia.Data.BindingValue`1" && p[4].ParameterType.MetadataType == MetadataType.Boolean)
                plans.Add(new(m, PropertyOperationKind.Notify, Priority: 3));
            else if (m.Name == "RaisePropertyChanged" && p.Count == 2 && p[0].ParameterType.GetElementType().FullName == "Avalonia.AvaloniaPropertyChangedEventArgs`1")
                plans.Add(new(m, PropertyOperationKind.Notify, ArgsNotification: true));
        }
        var effective = module.GetType("Avalonia.PropertyStore.EffectiveValue`1");
        if (effective?.Methods.SingleOrDefault(m => m.Name == "SetAndRaise" && m.Parameters.Count == 3) is { } entry &&
            entry.Parameters[0].ParameterType.FullName == "Avalonia.PropertyStore.ValueStore" && entry.Parameters[1].ParameterType.FullName == "Avalonia.PropertyStore.IValueEntry")
            plans.Add(new(entry, PropertyOperationKind.ValueEntry, Priority: 2, Entry: true));
        var setter = module.GetType("Avalonia.Styling.PropertySetterInstance`1");
        if (setter is not null)
            foreach (var m in setter.Methods.Where(m => m.Name is "Start" or "Activate" or "Deactivate" or "Dispose").ToArray())
                plans.Add(new(m, PropertyOperationKind.SetterInstance, Setter: true));
        var optional = module.GetType("Avalonia.Data.Optional`1"); var binding = module.GetType("Avalonia.Data.BindingValue`1");
        var args = module.GetType("Avalonia.AvaloniaPropertyChangedEventArgs`1"); var argsBase = module.GetType("Avalonia.AvaloniaPropertyChangedEventArgs");
        var valid = ValidEnums(module) && getters.All(Auto) && ValidValueStruct(optional, false) && ValidValueStruct(binding, true) &&
            new[] { "get_Property", "get_OldValue", "get_NewValue" }.All(n => Auto(args?.Methods.SingleOrDefault(m => m.Name == n))) &&
            new[] { "get_Priority", "get_IsEffectiveValueChange" }.All(n => Auto(argsBase?.Methods.SingleOrDefault(m => m.Name == n)));
        if (!valid) return plans.Select(p => new MethodReport(p.Method.MetadataToken.ToInt32(), p.Method.FullName, "Properties", "Skipped",
            Note: "Unsupported property notification metadata, priority enum or optional/binding value representation")).ToArray();
        var optionalReader = AddValueReader(module, optional!, false); var bindingReader = AddValueReader(module, binding!, true);
        var reports = new List<MethodReport>();
        foreach (var plan in plans)
        {
            var method = plan.Method; var reason = MethodRewriter.Unsupported(method);
            if (method.IsStatic || method.GenericParameters.Count > 1 || method.DeclaringType.GenericParameters.Count > 1) reason = "unsupported property generic shape";
            if (plan.Priority >= 0 && method.Parameters[plan.Priority].ParameterType.FullName != "Avalonia.Data.BindingPriority") reason = "unsupported priority contract";
            if (plan.Entry && (!Auto(module.GetType("Avalonia.PropertyStore.ValueStore")?.Methods.SingleOrDefault(m => m.Name == "get_Owner")) ||
                !Auto(module.GetType("Avalonia.PropertyStore.EffectiveValue")?.Methods.SingleOrDefault(m => m.Name == "get_Property")))) reason = "unsupported value-entry ownership";
            if (plan.Setter && (method.ReturnType.MetadataType != MetadataType.Void ||
                !new[] { "_target", "_styledProperty", "_directProperty", "_value" }.All(n => setter!.Fields.Any(f => f.Name == n && !f.IsStatic)))) reason = "unsupported setter storage";
            var site = SourceSiteBuilder.Create(module, method);
            if (reason is null)
            {
                MethodRewriter.InstrumentNotifications(module, method);
                Wrap(module, plan, SourceSiteSymbol.Encode(site), getters!, optionalReader, bindingReader);
            }
            reports.Add(new(method.MetadataToken.ToInt32(), method.FullName, "Properties", reason is null ? "PropertyOperation" : "Skipped", Note: reason, Source: site));
        }
        return reports.ToArray();
    }
    private static bool ValidEnums(ModuleDefinition module)
    {
        var priority = module.GetType("Avalonia.Data.BindingPriority");
        var expected = new Dictionary<string, int> { ["Animation"] = -1, ["LocalValue"] = 0, ["StyleTrigger"] = 1,
            ["Template"] = 2, ["Style"] = 3, ["Inherited"] = 4, ["Unset"] = int.MaxValue };
        return priority is { IsEnum: true } && expected.All(pair => priority.Fields.Any(f => f.Name == pair.Key && f.Constant is int value && value == pair.Value)) &&
            module.GetType("Avalonia.Data.BindingValueType")?.Fields.Any(f => f.Name == "HasValue" && f.Constant is int value && value == 256) == true;
    }
    private static bool IsProperty(TypeReference t) => t.GetElementType().FullName is "Avalonia.AvaloniaProperty" or "Avalonia.AvaloniaProperty`1" or "Avalonia.StyledProperty`1" or "Avalonia.DirectPropertyBase`1";
    private static bool ValidValueStruct(TypeDefinition? type, bool binding)
    {
        if (type is not { IsValueType: true, GenericParameters.Count: 1 } || !type.Fields.Any(f => f.Name == "_value" && f.FieldType is GenericParameter { Position: 0 })) return false;
        if (!binding) return type.Fields.Any(f => f.Name == "<HasValue>k__BackingField" && f.FieldType.MetadataType == MetadataType.Boolean) &&
            Auto(type.Methods.SingleOrDefault(m => m.Name == "get_HasValue"));
        var method = type.Methods.SingleOrDefault(m => m.Name == "get_HasValue");
        var code = method?.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        return Auto(type.Methods.SingleOrDefault(m => m.Name == "get_Type")) && code is { Length: 5 } &&
            code[0].OpCode == OpCodes.Ldarg_0 && code[1].Operand is MethodReference { Name: "get_Type" } &&
            code[2].OpCode == OpCodes.Ldc_I4 && (int)code[2].Operand == 256 &&
            code[3].Operand is MethodReference call && call.DeclaringType.FullName == "Avalonia.EnumExtensions" && call.Name == "HasAllFlags" && code[4].OpCode == OpCodes.Ret;
    }
    // The reader is injected into the original value type, so private-field access is legal and no getter can execute application code.
    private static MethodDefinition AddValueReader(ModuleDefinition module, TypeDefinition type, bool binding)
    {
        var self = Self(type); var reader = new MethodDefinition("__AvTracePropertyValue", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Void);
        reader.Parameters.Add(new("scope", ParameterAttributes.None, new ByReferenceType(module.ImportReference(typeof(PropertyOperationScope)))));
        reader.Parameters.Add(new("value", ParameterAttributes.None, self)); reader.Parameters.Add(new("oldValue", ParameterAttributes.None, module.TypeSystem.Boolean));
        type.Methods.Add(reader); var il = reader.Body.GetILProcessor(); var end = Instruction.Create(OpCodes.Ret);
        il.Append(Instruction.Create(OpCodes.Ldarga, reader.Parameters[1]));
        il.Append(Instruction.Create(OpCodes.Ldfld, Field(type.Fields.Single(f => f.Name == (binding ? "<Type>k__BackingField" : "<HasValue>k__BackingField")), self)));
        if (binding) { il.Append(Instruction.Create(OpCodes.Ldc_I4, 256)); il.Append(Instruction.Create(OpCodes.And)); }
        il.Append(Instruction.Create(OpCodes.Brfalse, end)); il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldarga, reader.Parameters[1])); il.Append(Instruction.Create(OpCodes.Ldfld, Field(type.Fields.Single(f => f.Name == "_value"), self)));
        il.Append(Instruction.Create(OpCodes.Ldarg_2)); il.Append(Instruction.Create(OpCodes.Call, ValueMethod(module, type.GenericParameters[0]))); il.Append(end);
        return reader;
    }
    private static GenericInstanceType Self(TypeDefinition type) { var self = new GenericInstanceType(type); foreach (var p in type.GenericParameters) self.GenericArguments.Add(p); return self; }
    private static FieldReference Field(FieldDefinition field, TypeReference self) => new(field.Name, field.FieldType, self);
    private static MethodReference Bound(MethodDefinition method, TypeReference host)
    {
        var result = new MethodReference(method.Name, method.ReturnType, host) { HasThis = method.HasThis, CallingConvention = method.CallingConvention };
        foreach (var p in method.Parameters) result.Parameters.Add(new(p.ParameterType)); return result;
    }
    private static MethodReference ValueMethod(ModuleDefinition module, TypeReference type)
    { var method = new GenericInstanceMethod(module.ImportReference(typeof(PropertyOperationScope).GetMethod(nameof(PropertyOperationScope.ObserveValue))!)); method.GenericArguments.Add(type); return method; }

    private static void Wrap(ModuleDefinition module, Plan plan, string site, MethodDefinition?[] getters, MethodDefinition optionalReader, MethodDefinition bindingReader)
    {
        var m = plan.Method; var body = m.Body; var il = body.GetILProcessor(); MethodRewriter.ExpandBranches(m); var first = body.Instructions[0];
        var scope = new VariableDefinition(module.ImportReference(typeof(PropertyOperationScope))); body.Variables.Add(scope);
        var prop = new VariableDefinition(module.GetType("Avalonia.AvaloniaProperty")); body.Variables.Add(prop);
        var result = m.ReturnType.MetadataType == MetadataType.Void ? null : new VariableDefinition(m.ReturnType); if (result is not null) body.Variables.Add(result);
        var report = Instruction.Create(OpCodes.Ldloca, scope); var end = Instruction.Create(OpCodes.Nop);
        foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            if (result is null) { ret.OpCode = OpCodes.Leave; ret.Operand = report; }
            else { ret.OpCode = OpCodes.Stloc; ret.Operand = result; il.InsertAfter(ret, Instruction.Create(OpCodes.Leave, report)); }
        }
        foreach (var h in body.ExceptionHandlers) { h.TryEnd ??= report; h.HandlerEnd ??= report; }
        il.Append(report);
        if (plan.Kind == PropertyOperationKind.DirectSetAndRaise)
        { il.Append(Instruction.Create(OpCodes.Ldloc, result!)); il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(PropertyOperationScope).GetMethod(nameof(PropertyOperationScope.DirectResult))!))); }
        else il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(PropertyOperationScope).GetMethod(nameof(PropertyOperationScope.Returned))!)));
        il.Append(Instruction.Create(OpCodes.Leave, end)); var cleanup = Instruction.Create(OpCodes.Ldloca, scope); il.Append(cleanup);
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(PropertyOperationScope).GetMethod(nameof(PropertyOperationScope.Dispose))!))); il.Append(Instruction.Create(OpCodes.Endfinally));
        il.Append(end); if (result is not null) il.Append(Instruction.Create(OpCodes.Ldloc, result)); il.Append(Instruction.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = end });
        var prefix = new List<Instruction>(); void Emit(OpCode op) => prefix.Add(Instruction.Create(op)); void Int(int n) => prefix.Add(Instruction.Create(OpCodes.Ldc_I4, n));
        void Arg(int n) => prefix.Add(Instruction.Create(OpCodes.Ldarg, m.Parameters[n])); void Call(MethodReference method) => prefix.Add(Instruction.Create(OpCodes.Call, method));
        void LoadField(string name) { Emit(OpCodes.Ldarg_0); prefix.Add(Instruction.Create(OpCodes.Ldfld, Field(m.DeclaringType.Fields.Single(f => f.Name == name), Self(m.DeclaringType)))); }
        if (plan.ArgsNotification)
        {
            var noArgs = Instruction.Create(OpCodes.Ldnull); var setProp = Instruction.Create(OpCodes.Stloc, prop);
            Arg(0); prefix.Add(Instruction.Create(OpCodes.Brfalse, noArgs)); Arg(0);
            Call(Bound(module.GetType("Avalonia.AvaloniaPropertyChangedEventArgs`1").Methods.Single(x => x.Name == "get_Property"), m.Parameters[0].ParameterType));
            prefix.Add(Instruction.Create(OpCodes.Br, setProp)); prefix.Add(noArgs); prefix.Add(setProp);
        }
        else if (plan.Entry) { Emit(OpCodes.Ldarg_0); Call(module.GetType("Avalonia.PropertyStore.EffectiveValue").Methods.Single(x => x.Name == "get_Property")); prefix.Add(Instruction.Create(OpCodes.Stloc, prop)); }
        else if (plan.Setter)
        {
            var setProp = Instruction.Create(OpCodes.Stloc, prop); LoadField("_styledProperty"); Emit(OpCodes.Dup);
            prefix.Add(Instruction.Create(OpCodes.Brtrue, setProp)); Emit(OpCodes.Pop); LoadField("_directProperty"); prefix.Add(setProp);
        }
        else { Arg(plan.Property); prefix.Add(Instruction.Create(OpCodes.Stloc, prop)); }
        prefix.Add(Instruction.Create(OpCodes.Ldstr, site)); Int((int)plan.Kind);
        if (plan.Entry) { Arg(0); Call(module.GetType("Avalonia.PropertyStore.ValueStore").Methods.Single(x => x.Name == "get_Owner")); }
        else if (plan.Setter) LoadField("_target"); else Emit(OpCodes.Ldarg_0);
        prefix.Add(Instruction.Create(OpCodes.Ldloc, prop));
        // Null metadata arguments preserve the original argument-validation path.
        var nullName = Instruction.Create(OpCodes.Ldnull); var named = Instruction.Create(OpCodes.Nop);
        prefix.Add(Instruction.Create(OpCodes.Ldloc, prop)); prefix.Add(Instruction.Create(OpCodes.Brfalse, nullName));
        prefix.Add(Instruction.Create(OpCodes.Ldloc, prop)); Call(getters[0]!); prefix.Add(Instruction.Create(OpCodes.Br, named)); prefix.Add(nullName); prefix.Add(named);
        if (plan.Entry) Arg(1); else if (plan.Setter) Emit(OpCodes.Ldarg_0); else Emit(OpCodes.Ldnull);
        if (plan.ArgsNotification)
        {
            var unknown = Instruction.Create(OpCodes.Ldc_I8, PropertyOperationEvent.UnknownPriority); var done = Instruction.Create(OpCodes.Nop);
            Arg(0); prefix.Add(Instruction.Create(OpCodes.Brfalse, unknown)); Arg(0); Call(module.GetType("Avalonia.AvaloniaPropertyChangedEventArgs").Methods.Single(x => x.Name == "get_Priority"));
            Emit(OpCodes.Conv_I8); prefix.Add(Instruction.Create(OpCodes.Br, done)); prefix.Add(unknown); prefix.Add(done);
        }
        else if (plan.Priority >= 0) { Arg(plan.Priority); Emit(OpCodes.Conv_I8); }
        else prefix.Add(Instruction.Create(OpCodes.Ldc_I8, plan.Kind is PropertyOperationKind.Set or PropertyOperationKind.Clear or PropertyOperationKind.DirectSetAndRaise ? 0L : PropertyOperationEvent.UnknownPriority));
        Int(0); var flagsReady = Instruction.Create(OpCodes.Nop);
        prefix.Add(Instruction.Create(OpCodes.Ldloc, prop)); prefix.Add(Instruction.Create(OpCodes.Brfalse, flagsReady));
        for (var n = 1; n < 4; n++) { prefix.Add(Instruction.Create(OpCodes.Ldloc, prop)); Call(getters[n]!); if (n > 1) { Int(n - 1); Emit(OpCodes.Shl); } Emit(OpCodes.Or); }
        prefix.Add(flagsReady);
        if (plan.Kind == PropertyOperationKind.Notify)
        {
            var effective = Instruction.Create(OpCodes.Ldc_I4, 8); var done = Instruction.Create(OpCodes.Or);
            if (plan.ArgsNotification) { Arg(0); prefix.Add(Instruction.Create(OpCodes.Brfalse, effective)); Arg(0); Call(module.GetType("Avalonia.AvaloniaPropertyChangedEventArgs").Methods.Single(x => x.Name == "get_IsEffectiveValueChange")); }
            else Arg(4);
            prefix.Add(Instruction.Create(OpCodes.Brtrue, effective)); Int(16); prefix.Add(Instruction.Create(OpCodes.Br, done)); prefix.Add(effective); prefix.Add(done);
        }
        Call(module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.BeginPropertyOperation))!)); prefix.Add(Instruction.Create(OpCodes.Stloc, scope));
        var valuesEnd = Instruction.Create(OpCodes.Nop);
        prefix.Add(Instruction.Create(OpCodes.Ldloca, scope)); Call(module.ImportReference(typeof(PropertyOperationScope).GetProperty(nameof(PropertyOperationScope.IsRecorded))!.GetMethod!));
        prefix.Add(Instruction.Create(OpCodes.Brfalse, valuesEnd));
        if (plan.Kind == PropertyOperationKind.Notify)
        {
            if (plan.ArgsNotification) { Arg(0); prefix.Add(Instruction.Create(OpCodes.Brfalse, valuesEnd)); }
            for (var n = 0; n < 2; n++)
            {
                prefix.Add(Instruction.Create(OpCodes.Ldloca, scope)); TypeReference valueType;
                if (plan.ArgsNotification)
                {
                    Arg(0); var getter = module.GetType("Avalonia.AvaloniaPropertyChangedEventArgs`1").Methods.Single(x => x.Name == (n == 0 ? "get_OldValue" : "get_NewValue"));
                    Call(Bound(getter, m.Parameters[0].ParameterType));
                    var closed = new GenericInstanceType((n == 0 ? optionalReader : bindingReader).DeclaringType); closed.GenericArguments.Add(m.GenericParameters[0]); valueType = closed;
                }
                else { Arg(n + 1); valueType = m.Parameters[n + 1].ParameterType; }
                Int(n == 0 ? 1 : 0); Call(Bound(n == 0 ? optionalReader : bindingReader, valueType));
            }
        }
        else if (plan.Input >= 0)
        {
            // For SetAndRaise, old state is supplied by the nested notification. Do not dereference the caller's field an extra time.
            prefix.Add(Instruction.Create(OpCodes.Ldloca, scope)); Arg(plan.Input); Int(0); Call(ValueMethod(module, m.Parameters[plan.Input].ParameterType));
        }
        else if (plan.Setter && m.Name is "Start" or "Activate")
        { prefix.Add(Instruction.Create(OpCodes.Ldloca, scope)); LoadField("_value"); Int(0); Call(ValueMethod(module, m.DeclaringType.GenericParameters[0])); }
        prefix.Add(valuesEnd); foreach (var i in prefix) il.InsertBefore(first, i);
        body.InitLocals = true; body.MaxStackSize = Math.Max(body.MaxStackSize, 16);
    }
}
