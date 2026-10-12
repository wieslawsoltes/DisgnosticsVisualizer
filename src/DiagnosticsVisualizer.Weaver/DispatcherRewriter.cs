using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>A shape-checked adapter for Avalonia's operation queue. No delegate replacement, task observation, or scheduler changes.</summary>
internal static class DispatcherRewriter
{
    public static MethodReport[] Instrument(ModuleDefinition module)
    {
        var operation = module.GetType("Avalonia.Threading.DispatcherOperation");
        var priority = module.GetType("Avalonia.Threading.DispatcherPriority");
        var queue = module.GetType("Avalonia.Threading.DispatcherPriorityQueue");
        if (operation is null || priority is null || queue is null) return [];
        var enqueue = queue.Methods.FirstOrDefault(m => m.Name == "Enqueue" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == priority.FullName && m.Parameters[1].ParameterType.FullName == operation.FullName &&
            m.ReturnType.FullName == operation.FullName && !m.IsStatic);
        var inactive = priority.Fields.FirstOrDefault(f => f.Name == "Inactive" && f.IsStatic && f.FieldType.FullName == priority.FullName);
        var changePriority = queue.Methods.FirstOrDefault(m => m.Name == "ChangeItemPriority" && !m.IsStatic && m.ReturnType.MetadataType == MetadataType.Void &&
            m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == operation.FullName && m.Parameters[1].ParameterType.FullName == priority.FullName);
        var getPriority = operation.Methods.FirstOrDefault(m => m.Name == "get_Priority" && !m.IsVirtual && m.Parameters.Count == 0 && m.ReturnType.FullName == priority.FullName);
        var getValue = priority.Methods.FirstOrDefault(m => m.Name == "get_Value" && m.Parameters.Count == 0 && m.ReturnType.MetadataType == MetadataType.Int32);
        var abort = operation.Methods.FirstOrDefault(m => m.Name == "CallAbortCallbacks" && !m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.MetadataType == MetadataType.Void);
        var callbacks = module.Types.Where(t => t.Namespace == "Avalonia.Threading" &&
            t.Name is "DispatcherOperation" or "DispatcherOperation`1" or "SendOrPostCallbackDispatcherOperation")
            .SelectMany(t => t.Methods.Where(m => m.Name == "InvokeCore" && !m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.MetadataType == MetadataType.Void)).ToArray();
        // Validate the complete adapter before any mutation. Unknown release shapes must not get half a lifecycle.
        if (enqueue is null || inactive is null || changePriority is null || MethodRewriter.Unsupported(changePriority) is not null || getPriority is null || getValue is null || abort is null || callbacks.Length != 3 ||
            MethodRewriter.Unsupported(enqueue) is not null || MethodRewriter.Unsupported(abort) is not null ||
            callbacks.Any(m => MethodRewriter.Unsupported(m) is not null ||
                m.Body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception") != 1))
            return [new(0, "Avalonia dispatcher queue adapter", "Dispatcher", "Skipped", 0, "Unrecognized queue/priority/callback shape; no lifecycle hooks emitted.")];
        var reports = new List<MethodReport>();
        var enqueueSite = SourceSiteBuilder.Create(module, enqueue);
        foreach (var ret in enqueue.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            // Original return value remains on the stack. This executes only after successful admission.
            ret.OpCode = OpCodes.Dup; ret.Operand = null; var tail = ret; var il = enqueue.Body.GetILProcessor();
            foreach (var i in new[] { Instruction.Create(OpCodes.Ldarga, enqueue.Parameters[0]), Instruction.Create(OpCodes.Call, getValue),
                Instruction.Create(OpCodes.Ldsflda, inactive), Instruction.Create(OpCodes.Call, getValue),
                Instruction.Create(OpCodes.Ldstr, SourceSiteSymbol.Encode(enqueueSite)),
                Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.DispatcherQueued))!)), Instruction.Create(OpCodes.Ret) })
            { il.InsertAfter(tail, i); tail = i; }
        }
        MethodRewriter.ExpandBranches(enqueue); enqueue.Body.MaxStackSize += 4;
        reports.Add(Report(enqueue, "Queue admission", enqueueSite));
        foreach (var ret in changePriority.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            ret.OpCode = OpCodes.Ldarg; ret.Operand = changePriority.Parameters[0]; var tail = ret; var il = changePriority.Body.GetILProcessor();
            foreach (var i in new[] { Instruction.Create(OpCodes.Ldarga, changePriority.Parameters[1]), Instruction.Create(OpCodes.Call, getValue),
                Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.DispatcherPriorityChanged))!)), Instruction.Create(OpCodes.Ret) })
            { il.InsertAfter(tail, i); tail = i; }
        }
        MethodRewriter.ExpandBranches(changePriority); changePriority.Body.MaxStackSize += 3;
        reports.Add(Report(changePriority, "Queued priority transition", SourceSiteBuilder.Create(module, changePriority)));
        foreach (var method in callbacks)
        {
            var site = SourceSiteBuilder.Create(module, method);
            WrapCallback(module, method, getPriority, getValue, SourceSiteSymbol.Encode(site));
            reports.Add(Report(method, "Callback entry, caught exception, finally completion", site));
        }
        var start = abort.Body.Instructions[0]; var processor = abort.Body.GetILProcessor();
        processor.InsertBefore(start, Instruction.Create(OpCodes.Ldarg_0));
        processor.InsertBefore(start, Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.DispatcherAborted))!)));
        abort.Body.MaxStackSize += 1;
        reports.Add(Report(abort, "Accepted abort before callbacks", SourceSiteBuilder.Create(module, abort)));
        return reports.ToArray();
    }
    private static MethodReport Report(MethodDefinition method, string detail, SourceSite site) =>
        new(method.MetadataToken.ToInt32(), method.FullName, "Dispatcher", "DispatcherLifecycle", 0, detail, site);
    private static void WrapCallback(ModuleDefinition module, MethodDefinition method, MethodDefinition getPriority, MethodDefinition getValue, string site)
    {
        var body = method.Body; var il = body.GetILProcessor(); MethodRewriter.ExpandBranches(method);
        var scope = new VariableDefinition(module.ImportReference(typeof(DispatcherExecutionScope))); body.Variables.Add(scope);
        var priority = new VariableDefinition(getPriority.ReturnType); body.Variables.Add(priority);
        var fault = body.ExceptionHandlers.Single(h => h.HandlerType == ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception");
        var handlerStart = fault.HandlerStart;
        var hook = new[] { Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.DispatcherFault))!)) };
        MethodRewriter.RedirectTargets(method, handlerStart, hook[0]);
        foreach (var i in hook) il.InsertBefore(handlerStart, i);
        var first = body.Instructions[0]; var epilogue = Instruction.Create(OpCodes.Ret);
        foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray()) { ret.OpCode = OpCodes.Leave; ret.Operand = epilogue; }
        var finallyStart = Instruction.Create(OpCodes.Ldloca, scope);
        foreach (var h in body.ExceptionHandlers) { h.TryEnd ??= finallyStart; h.HandlerEnd ??= finallyStart; }
        il.Append(finallyStart); il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(DispatcherExecutionScope).GetMethod(nameof(DispatcherExecutionScope.Dispose))!)));
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(epilogue);
        body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = epilogue });
        foreach (var i in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, getPriority), Instruction.Create(OpCodes.Stloc, priority),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldloca, priority), Instruction.Create(OpCodes.Call, getValue),
            Instruction.Create(OpCodes.Ldstr, site), Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.DispatcherStarted))!)), Instruction.Create(OpCodes.Stloc, scope) }) il.InsertBefore(first, i);
        body.InitLocals = true; body.MaxStackSize += 4;
    }
}
