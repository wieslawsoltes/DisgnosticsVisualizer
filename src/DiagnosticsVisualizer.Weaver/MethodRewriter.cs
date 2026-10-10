using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

internal static class MethodRewriter
{
    public static string? Unsupported(MethodDefinition method)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0) return "no IL body";
        if (method.IsConstructor) return "constructor scope excluded";
        if (method.DeclaringType.IsValueType) return "value-type receiver excluded";
        if (method.ReturnType.IsByReference || method.ReturnType.IsPointer || method.ReturnType.MetadataType == MetadataType.TypedByReference)
            return "by-reference/pointer return excluded";
        if (method.Body.Instructions.Any(i => i.OpCode == OpCodes.Jmp || i.OpCode == OpCodes.Tail)) return "jmp/tailcall excluded";
        return null;
    }
    public static int InstrumentNotifications(ModuleDefinition module, MethodDefinition method)
    {
        if (!method.HasBody) return 0;
        var processor = method.Body.GetILProcessor(); var count = 0;
        foreach (var instruction in method.Body.Instructions.ToArray())
        {
            if (instruction.Operand is not MethodReference call || call.Name != "Invoke" || call.Parameters.Count != 2 ||
                instruction.OpCode != OpCodes.Callvirt) continue;
            string? probe = call.DeclaringType.FullName switch
            {
                "System.ComponentModel.PropertyChangedEventHandler" => nameof(Probe.PropertyNotification),
                "System.Collections.Specialized.NotifyCollectionChangedEventHandler" => nameof(Probe.CollectionNotification),
                _ => null
            };
            if (probe is null || instruction.Previous?.OpCode.OpCodeType == OpCodeType.Prefix) continue;
            var sender = new VariableDefinition(module.ImportReference(call.Parameters[0].ParameterType));
            var args = new VariableDefinition(module.ImportReference(call.Parameters[1].ParameterType));
            method.Body.Variables.Add(sender); method.Body.Variables.Add(args);
            var injected = new[]
            {
                Instruction.Create(OpCodes.Stloc, args), Instruction.Create(OpCodes.Stloc, sender),
                Instruction.Create(OpCodes.Ldloc, sender), Instruction.Create(OpCodes.Ldloc, args),
                Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(probe)!)),
                Instruction.Create(OpCodes.Ldloc, sender), Instruction.Create(OpCodes.Ldloc, args)
            };
            RedirectTargets(method, instruction, injected[0]);
            foreach (var added in injected) processor.InsertBefore(instruction, added);
            count++;
        }
        if (count > 0) { method.Body.InitLocals = true; ExpandBranches(method); }
        return count;
    }
    private static void RedirectTargets(MethodDefinition method, Instruction oldTarget, Instruction newTarget)
    {
        foreach (var i in method.Body.Instructions)
        {
            if (ReferenceEquals(i.Operand, oldTarget)) i.Operand = newTarget;
            if (i.Operand is Instruction[] targets)
                for (var n = 0; n < targets.Length; n++) if (ReferenceEquals(targets[n], oldTarget)) targets[n] = newTarget;
        }
        foreach (var h in method.Body.ExceptionHandlers)
        {
            if (h.TryStart == oldTarget) h.TryStart = newTarget;
            if (h.TryEnd == oldTarget) h.TryEnd = newTarget;
            if (h.HandlerStart == oldTarget) h.HandlerStart = newTarget;
            if (h.HandlerEnd == oldTarget) h.HandlerEnd = newTarget;
            if (h.FilterStart == oldTarget) h.FilterStart = newTarget;
        }
    }
    public static void Wrap(ModuleDefinition module, MethodDefinition method, Subsystem subsystem)
    {
        var body = method.Body; var il = body.GetILProcessor(); ExpandBranches(method);
        var originalStart = body.Instructions[0];
        var scope = new VariableDefinition(module.ImportReference(typeof(TraceScope))); body.Variables.Add(scope);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void)
        { result = new VariableDefinition(method.ReturnType); body.Variables.Add(result); }
        var epilogue = Instruction.Create(OpCodes.Nop);
        foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            if (result is null) { ret.OpCode = OpCodes.Leave; ret.Operand = epilogue; }
            else { ret.OpCode = OpCodes.Stloc; ret.Operand = result; il.InsertAfter(ret, Instruction.Create(OpCodes.Leave, epilogue)); }
        }
        var finallyStart = Instruction.Create(OpCodes.Ldloca, scope);
        foreach (var handler in body.ExceptionHandlers)
        {
            handler.TryEnd ??= finallyStart;
            handler.HandlerEnd ??= finallyStart;
        }
        il.Append(finallyStart);
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(TraceScope).GetMethod(nameof(TraceScope.Dispose))!)));
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(epilogue);
        if (result is not null) il.Append(Instruction.Create(OpCodes.Ldloc, result));
        il.Append(Instruction.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally)
        { TryStart = originalStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = epilogue });
        var related = method.Parameters.FirstOrDefault(p => !p.ParameterType.IsValueType &&
            !p.ParameterType.IsByReference && !p.ParameterType.IsPointer && !p.ParameterType.IsGenericParameter);
        var prelude = new[]
        {
            Instruction.Create(OpCodes.Ldc_I4, (int)subsystem), Instruction.Create(OpCodes.Ldstr, method.FullName),
            method.IsStatic ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldarg_0),
            related is null ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldarg, related),
            Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.Enter))!)),
            Instruction.Create(OpCodes.Stloc, scope)
        };
        foreach (var instruction in prelude) il.InsertBefore(originalStart, instruction);
        body.InitLocals = true; body.MaxStackSize = Math.Max(body.MaxStackSize, 8);
    }
    private static void ExpandBranches(MethodDefinition method)
    {
        foreach (var i in method.Body.Instructions)
            i.OpCode = i.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave, _ => i.OpCode
            };
    }
}
