using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Standard compiler state-machine lifecycle instrumentation; never rewrites awaitables.</summary>
internal static class AsyncStateMachineRewriter
{
    private const string TokenField = "__avtraceAsyncOperation";
    private static readonly HashSet<string> Builders = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1",
        "System.Runtime.CompilerServices.AsyncVoidMethodBuilder"
    };

    public static MethodReport? Instrument(ModuleDefinition module, MethodDefinition kickoff, Subsystem category)
    {
        var attribute = kickoff.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is not TypeReference reference) return null;
        var source = SourceSiteBuilder.Create(module, kickoff);
        MethodReport Skipped(string reason) => new(kickoff.MetadataToken.ToInt32(), kickoff.FullName,
            category.ToString(), "Skipped", Note: "Async lifecycle: " + reason, Source: source);
        TypeDefinition? machine;
        try { machine = reference.Resolve(); }
        catch (AssemblyResolutionException) { return Skipped("state machine could not be resolved"); }
        if (machine?.Module != module) return Skipped("state machine is not in this module");
        var move = machine.Methods.FirstOrDefault(m => m.Name == "MoveNext" && m.Parameters.Count == 0 && m.HasBody);
        var builder = machine.Fields.FirstOrDefault(f => f.Name == "<>t__builder");
        if (move is null || builder is null || !Builders.Contains(builder.FieldType.GetElementType().FullName))
            return Skipped("unsupported custom builder or state-machine shape");
        if (machine.Fields.Any(f => f.Name == TokenField)) return Skipped("state machine already has lifecycle hooks");
        var terminal = move.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference call &&
            call.DeclaringType.FullName == builder.FieldType.FullName &&
            (call.Name == "SetResult" || (call.Name == "SetException" && call.Parameters.Count == 1 &&
                call.Parameters[0].ParameterType.FullName == "System.Exception"))).ToArray();
        // An always-throwing async method has no SetResult path after compiler optimization.
        if (terminal.Length == 0) return Skipped("no standard terminal transitions");

        var token = new FieldDefinition(TokenField, FieldAttributes.Private, module.ImportReference(typeof(AsyncOperationToken)));
        machine.Fields.Add(token);
        // Generic state machines must address fields through their instantiated declaring type,
        // not an open TypeDef token. This also covers generic methods in generic classes.
        TypeReference self = machine;
        if (machine.HasGenericParameters)
        {
            var instance = new GenericInstanceType(machine);
            foreach (var parameter in machine.GenericParameters) instance.GenericArguments.Add(parameter);
            self = instance;
        }
        FieldReference Field(FieldDefinition field) => new(field.Name, field.FieldType, self);
        var tokenReference = Field(token);
        var begin = module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.AsyncBegin))!);
        var complete = module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.AsyncComplete))!);
        var il = move.Body.GetILProcessor();
        var receiver = machine.Fields.FirstOrDefault(f => f.Name == "<>4__this" &&
            !f.FieldType.IsValueType && !f.FieldType.IsGenericParameter);
        var prelude = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldflda, tokenReference),
            Instruction.Create(OpCodes.Ldc_I4, (int)category), Instruction.Create(OpCodes.Ldstr, SourceSiteSymbol.Encode(source))
        };
        if (receiver is null) prelude.Add(Instruction.Create(OpCodes.Ldnull));
        else { prelude.Add(Instruction.Create(OpCodes.Ldarg_0)); prelude.Add(Instruction.Create(OpCodes.Ldfld, Field(receiver))); }
        prelude.Add(Instruction.Create(OpCodes.Call, begin));
        var first = move.Body.Instructions[0];
        foreach (var instruction in prelude) il.InsertBefore(first, instruction);
        foreach (var instruction in terminal)
        {
            var injected = new List<Instruction>();
            VariableDefinition? error = null;
            if (((MethodReference)instruction.Operand).Name == "SetException")
            {
                error = new(module.ImportReference(typeof(Exception))); move.Body.Variables.Add(error);
                // Preserve the original exception and builder reference on the application stack.
                injected.Add(Instruction.Create(OpCodes.Stloc, error));
            }
            injected.Add(Instruction.Create(OpCodes.Ldarg_0)); injected.Add(Instruction.Create(OpCodes.Ldflda, tokenReference));
            injected.Add(error is null ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldloc, error));
            injected.Add(Instruction.Create(OpCodes.Call, complete));
            if (error is not null) injected.Add(Instruction.Create(OpCodes.Ldloc, error));
            MethodRewriter.RedirectTargets(move, instruction, injected[0]);
            foreach (var added in injected) il.InsertBefore(instruction, added);
        }
        MethodRewriter.ExpandBranches(move); move.Body.InitLocals = true; move.Body.MaxStackSize += 4;
        return new(move.MetadataToken.ToInt32(), move.FullName, category.ToString(), "AsyncLifecycle",
            Note: $"{terminal.Length} standard builder transitions; runtime opt-in", Source: source);
    }
}
