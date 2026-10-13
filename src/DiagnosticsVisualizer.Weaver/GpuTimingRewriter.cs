using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Complete shape validation precedes edits. Uses the original already-read context local.</summary>
internal static class GpuTimingRewriter
{
    public static MethodReport[] Instrument(ModuleDefinition module, string directory, IAssemblyResolver resolver)
    {
        if (module.Assembly.Name.Name != "Avalonia.Skia") return [];
        var target = module.Types.FirstOrDefault(t => t.FullName == "Avalonia.Skia.GlRenderTarget");
        var session = target?.NestedTypes.FirstOrDefault(t => t.Name == "GlGpuSession");
        var gpu = module.Types.FirstOrDefault(t => t.FullName == "Avalonia.Skia.GlSkiaGpu");
        var begin = target?.Methods.FirstOrDefault(m => m.Name == "BeginRenderingSessionCore" && m.HasBody);
        var end = session?.Methods.FirstOrDefault(m => m.Name == "Dispose" && !m.HasParameters && m.HasBody);
        var dispose = gpu?.Methods.FirstOrDefault(m => m.Name == "Dispose" && !m.HasParameters && m.HasBody);
        var context = gpu?.Fields.FirstOrDefault(f => f.Name == "_glContext" && f.FieldType.FullName == "Avalonia.OpenGL.IGlContext");
        var local = begin?.Body.Variables.Where(v => v.VariableType.FullName == "Avalonia.OpenGL.IGlContext").ToArray() ?? [];
        var construction = begin?.Body.Instructions.Where(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && m.DeclaringType.FullName == session?.FullName).ToArray() ?? [];
        var release = end?.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference { Name: "Dispose" } &&
            i.Previous?.OpCode == OpCodes.Ldfld && i.Previous.Operand is FieldReference { Name: "_glSession" }).ToArray() ?? [];
        var flush = end?.Body.Instructions.Where(i => i.Operand is MethodReference { Name: "Flush", DeclaringType.FullName: "SkiaSharp.GRContext" }).ToArray() ?? [];
        MethodReport Skip(string reason) => new(begin?.MetadataToken.ToInt32() ?? 0, "Avalonia.Skia GL rendering", "Rendering", "Skipped", 0, reason);
        if (module.Assembly.Name.Version != new Version(11, 3, 22, 0) || session is null || begin is null || end is null || dispose is null || context is null ||
            local.Length != 1 || construction.Length != 1 || release.Length != 1 || flush.Length != 1 ||
            end.Body.ExceptionHandlers.Count != 0 || end.ReturnType.MetadataType != MetadataType.Void ||
            end.Body.Instructions.IndexOf(flush[0]) >= end.Body.Instructions.IndexOf(release[0]) ||
            session.Fields.Any(f => f.Name == "__AvTraceGpuQuery")) return [Skip("Unsupported or already edited Skia OpenGL session shape.")];
        var agentPath = Path.Combine(directory, "DiagnosticsVisualizer.Agent.Avalonia.dll");
        if (!File.Exists(agentPath)) return [Skip("GPU backend assembly is not present in application output.")];
        using var agent = AssemblyDefinition.ReadAssembly(agentPath, new ReaderParameters { AssemblyResolver = resolver });
        var abi = agent.MainModule.Types.FirstOrDefault(t => t.FullName == "DiagnosticsVisualizer.Agent.Avalonia.OpenGlGpuDiagnostics");
        var startHook = abi?.Methods.SingleOrDefault(m => m.Name == "Begin" && m.Parameters.Count == 1 && m.IsStatic && m.Parameters[0].ParameterType.MetadataType == MetadataType.Object);
        var submitHook = abi?.Methods.SingleOrDefault(m => m.Name == "Submit" && m.Parameters.Count == 1 && m.IsStatic);
        var abortHook = abi?.Methods.SingleOrDefault(m => m.Name == "Abort" && m.Parameters.Count == 1 && m.IsStatic);
        var releaseHook = abi?.Methods.SingleOrDefault(m => m.Name == "Release" && m.Parameters.Count == 1 && m.IsStatic);
        if (startHook?.ReturnType.FullName != typeof(GpuSubmission).FullName || submitHook?.Parameters[0].ParameterType.FullName != typeof(GpuSubmission).FullName + "&" ||
            abortHook?.Parameters[0].ParameterType.FullName != typeof(GpuSubmission).FullName + "&" || releaseHook is null)
            return [Skip("GPU backend ABI does not match the weaver.")];
        var field = new FieldDefinition("__AvTraceGpuQuery", FieldAttributes.Assembly, module.ImportReference(typeof(GpuSubmission)));
        session.Fields.Add(field);
        var il = begin.Body.GetILProcessor(); var cursor = construction[0];
        foreach (var i in new[] { Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldloc, local[0]),
            Instruction.Create(OpCodes.Call, module.ImportReference(startHook)), Instruction.Create(OpCodes.Stfld, field) })
        { il.InsertAfter(cursor, i); cursor = i; }
        begin.Body.MaxStackSize += 2;
        il = end.Body.GetILProcessor();
        // The original platform-session receiver stays on the stack below this balanced call.
        foreach (var i in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldflda, field), Instruction.Create(OpCodes.Call, module.ImportReference(submitHook!)) }) il.InsertBefore(release[0], i);
        var tryStart = end.Body.Instructions[0]; var done = Instruction.Create(OpCodes.Ret);
        foreach (var i in end.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray()) { i.OpCode = OpCodes.Leave; i.Operand = done; }
        var finallyStart = Instruction.Create(OpCodes.Ldarg_0); il.Append(finallyStart);
        il.Append(Instruction.Create(OpCodes.Ldflda, field)); il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(abortHook!)));
        il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(done);
        end.Body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = done });
        end.Body.MaxStackSize += 2;
        il = dispose.Body.GetILProcessor(); var first = dispose.Body.Instructions[0];
        foreach (var i in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, context), Instruction.Create(OpCodes.Call, module.ImportReference(releaseHook)) }) il.InsertBefore(first, i);
        return new[] { begin, end, dispose }.Select(m => new MethodReport(m.MetadataToken.ToInt32(), m.FullName, "Rendering", "GpuTimestamp", 0, null, SourceSiteBuilder.Create(module, m))).ToArray();
    }
}
