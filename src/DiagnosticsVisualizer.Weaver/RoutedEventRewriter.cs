using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Preserves the original route loop, predicates, delegate instances and DynamicInvoke exception behavior.</summary>
internal static class RoutedEventRewriter
{
    private const string Prefix = "__AvTraceRoute_";
    public static MethodReport[] Instrument(ModuleDefinition module)
    {
        var type = module.GetType("Avalonia.Interactivity.EventRoute");
        if (type is null) return [];
        var args = module.GetType("Avalonia.Interactivity.RoutedEventArgs");
        var routed = module.GetType("Avalonia.Interactivity.RoutedEvent");
        var item = type.NestedTypes.UniqueOrDefault(t => t.Name == "RouteItem");
        var raise = type.Methods.UniqueOrDefault(m => m.Name == "RaiseEvent" && m.Parameters.Count == 2);
        var loop = type.Methods.UniqueOrDefault(m => m.Name == "RaiseEventImpl" && m.Parameters.Count == 1);
        var field = type.Fields.UniqueOrDefault(f => f.Name == "_event");
        MethodDefinition? Getter(TypeDefinition? t, string name) => t?.Methods.UniqueOrDefault(m => m.Name == "get_" + name &&
            !m.IsVirtual && !m.IsStatic && m.Parameters.Count == 0 && m.HasBody &&
            m.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).Select(i => i.OpCode.Code).SequenceEqual(new[] { Code.Ldarg_0, Code.Ldfld, Code.Ret }));
        var source = Getter(args, "Source"); var handled = Getter(args, "Handled"); var route = Getter(args, "Route");
        var name = Getter(routed, "Name"); var strategies = Getter(routed, "RoutingStrategies");
        var handler = Getter(item, "Handler"); var target = Getter(item, "Target");
        var routes = Getter(item, "Routes"); var too = Getter(item, "HandledEventsToo");
        var setSource = raise?.Body?.Instructions.UniqueOrDefault(i => i.Operand is MethodReference m && m.DeclaringType.FullName == args?.FullName && m.Name == "set_Source");
        var local = loop?.Body?.Variables.UniqueOrDefault(v => v.VariableType.FullName == item?.FullName);
        var classCall = loop?.Body?.Instructions.UniqueOrDefault(i => i.Operand is MethodReference m && m.DeclaringType.FullName == routed?.FullName && m.Name == "InvokeRaised");
        var adapterCall = loop?.Body?.Instructions.UniqueOrDefault(i => i.Operand is MethodReference m &&
            m.DeclaringType.FullName == "System.Action`3<System.Delegate,System.Object,Avalonia.Interactivity.RoutedEventArgs>" && m.Name == "Invoke");
        var dynamicCall = loop?.Body?.Instructions.UniqueOrDefault(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Delegate" &&
            m.Name == "DynamicInvoke" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Object[]");
        var candidate = loop?.Body?.Instructions.FirstOrDefault(i => i.Operand is MethodReference m && m.FullName == handler?.FullName)?.Previous;
        // Inspect the entire supported shape before adding any methods or IL.
        if (args is null || routed is null || item is null || raise is null || loop is null || field?.FieldType.FullName != routed.FullName ||
            source?.ReturnType.MetadataType != MetadataType.Object || raise.ReturnType.MetadataType != MetadataType.Void ||
            raise.Parameters[0].ParameterType.FullName != "Avalonia.Interactivity.Interactive" || raise.Parameters[1].ParameterType.FullName != args.FullName ||
            loop.Parameters[0].ParameterType.FullName != args.FullName || loop.ReturnType.MetadataType != MetadataType.Void ||
            route?.ReturnType.FullName != "Avalonia.Interactivity.RoutingStrategies" || strategies?.ReturnType.FullName != "Avalonia.Interactivity.RoutingStrategies" ||
            handler?.ReturnType.FullName != "System.Delegate" || target?.ReturnType.FullName != "Avalonia.Interactivity.Interactive" ||
            routes?.ReturnType.FullName != "Avalonia.Interactivity.RoutingStrategies" || too?.ReturnType.MetadataType != MetadataType.Boolean ||
            handled?.ReturnType.MetadataType != MetadataType.Boolean || route is null || name?.ReturnType.MetadataType != MetadataType.String ||
            strategies is null || handler is null || target is null || routes is null || too is null || setSource is null || local is null ||
            classCall?.OpCode != OpCodes.Callvirt || adapterCall?.OpCode != OpCodes.Callvirt || dynamicCall?.OpCode != OpCodes.Callvirt ||
            !CallShape(classCall, 2, MetadataType.Void) || !CallShape(adapterCall, 3, MetadataType.Void) || !CallShape(dynamicCall, 1, MetadataType.Object) ||
            candidate?.Operand != local || candidate.OpCode.Code is not (Code.Ldloca or Code.Ldloca_S) ||
            MethodRewriter.Unsupported(raise) is not null || MethodRewriter.Unsupported(loop) is not null ||
            type.Methods.Any(m => m.Name.StartsWith(Prefix, StringComparison.Ordinal)))
            return [new(0, "Avalonia routed-event adapter", "RoutedEvents", "Skipped", 0, "Unrecognized route/args/delegate shape; no semantic hooks emitted.")];
        var originalRaise = SourceSiteBuilder.Create(module, raise); var originalLoop = SourceSiteBuilder.Create(module, loop);
        var emit = loop.Body.GetILProcessor();
        var candidateProbe = new[]
        {
            Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Call, handler),
            Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Call, target), Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, source),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, route),
            Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Call, routes),
            Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, handled),
            Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Call, too), Call(module, nameof(Probe.RoutedCandidate))
        };
        MethodRewriter.RedirectTargets(loop, candidate, candidateProbe[0]);
        foreach (var i in candidateProbe) emit.InsertBefore(candidate, i);
        Replace(classCall, Bridge("Class", (MethodReference)classCall.Operand, routed, 1, 2, -1, -1, []), []);
        Replace(adapterCall, Bridge("Adapter", (MethodReference)adapterCall.Operand, ((MethodReference)adapterCall.Operand).DeclaringType,
            2, 3, 1, 5, [module.TypeSystem.Int32, module.TypeSystem.Boolean]), Extra(false));
        Replace(dynamicCall, Bridge("Dynamic", (MethodReference)dynamicCall.Operand, module.ImportReference(typeof(Delegate)),
            3, 2, 0, 5, [args, module.TypeSystem.Object, module.TypeSystem.Int32, module.TypeSystem.Boolean]), Extra(true));
        WrapRaise();
        MethodRewriter.ExpandBranches(loop); loop.Body.MaxStackSize += 8;
        return [new(raise.MetadataToken.ToInt32(), raise.FullName, "RoutedEvents", "RoutedEventDispatch", 0, "Dispatch admission and synchronous completion", originalRaise),
            new(loop.MetadataToken.ToInt32(), loop.FullName, "RoutedEvents", "RoutedEventDispatch", 0, "Class-listener/adapter/DynamicInvoke boundaries, handled and route skips", originalLoop)];

        Instruction[] Extra(bool dynamic)
        {
            var list = new List<Instruction>();
            if (dynamic) { list.Add(Instruction.Create(OpCodes.Ldarg_1)); list.Add(Instruction.Create(OpCodes.Ldloca, local)); list.Add(Instruction.Create(OpCodes.Call, target)); }
            list.Add(Instruction.Create(OpCodes.Ldloca, local)); list.Add(Instruction.Create(OpCodes.Call, routes));
            list.Add(Instruction.Create(OpCodes.Ldloca, local)); list.Add(Instruction.Create(OpCodes.Call, too)); return list.ToArray();
        }
        void Replace(Instruction call, MethodDefinition bridge, Instruction[] extra)
        {
            if (extra.Length > 0) { MethodRewriter.RedirectTargets(loop, call, extra[0]); foreach (var i in extra) emit.InsertBefore(call, i); }
            call.OpCode = OpCodes.Call; call.Operand = bridge;
        }
        MethodDefinition Bridge(string suffix, MethodReference original, TypeReference receiver, int targetIndex, int argsIndex,
            int handlerIndex, int tooIndex, TypeReference[] extras)
        {
            var method = new MethodDefinition(Prefix + suffix, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, original.ReturnType);
            method.Parameters.Add(new("receiver", ParameterAttributes.None, receiver));
            // A nongeneric bridge needs closed parameter types for Action<T1,T2,T3>.Invoke.
            var parameters = receiver is GenericInstanceType g ? g.GenericArguments.ToArray() : original.Parameters.Select(p => p.ParameterType).ToArray();
            foreach (var p in parameters.Concat(extras)) method.Parameters.Add(new(p));
            type.Methods.Add(method); var body = method.Body; var il = body.GetILProcessor();
            var scope = new VariableDefinition(module.ImportReference(typeof(RoutedHandlerScope))); body.Variables.Add(scope);
            var returned = new VariableDefinition(module.TypeSystem.Boolean); body.Variables.Add(returned);
            VariableDefinition? result = null;
            if (original.ReturnType.MetadataType != MetadataType.Void) { result = new(original.ReturnType); body.Variables.Add(result); }
            void Load(int n) => il.Append(Instruction.Create(OpCodes.Ldarg, method.Parameters[n]));
            if (handlerIndex < 0) il.Append(Instruction.Create(OpCodes.Ldnull)); else Load(handlerIndex);
            Load(targetIndex); Load(argsIndex); Load(argsIndex); il.Append(Instruction.Create(OpCodes.Call, source));
            Load(argsIndex); il.Append(Instruction.Create(OpCodes.Call, route));
            Load(argsIndex); il.Append(Instruction.Create(OpCodes.Call, handled));
            if (tooIndex < 0) il.Append(Instruction.Create(OpCodes.Ldc_I4_0)); else Load(tooIndex);
            il.Append(Instruction.Create(handlerIndex < 0 ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            il.Append(Call(module, nameof(Probe.BeginRoutedHandler))); il.Append(Instruction.Create(OpCodes.Stloc, scope));
            var begin = Instruction.Create(OpCodes.Ldarg, method.Parameters[0]); il.Append(begin);
            for (var n = 1; n <= original.Parameters.Count; n++) Load(n);
            il.Append(Instruction.Create(OpCodes.Callvirt, original)); if (result is not null) il.Append(Instruction.Create(OpCodes.Stloc, result));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1)); il.Append(Instruction.Create(OpCodes.Stloc, returned));
            var end = Instruction.Create(OpCodes.Nop); il.Append(Instruction.Create(OpCodes.Leave, end));
            var cleanup = Instruction.Create(OpCodes.Ldloca, scope); il.Append(cleanup);
            Load(argsIndex); il.Append(Instruction.Create(OpCodes.Call, handled)); il.Append(Instruction.Create(OpCodes.Ldloc, returned));
            il.Append(Call(module, nameof(Probe.EndRoutedHandler))); il.Append(Instruction.Create(OpCodes.Endfinally)); il.Append(end);
            if (result is not null) il.Append(Instruction.Create(OpCodes.Ldloc, result)); il.Append(Instruction.Create(OpCodes.Ret));
            body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = begin, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = end });
            body.InitLocals = true; body.MaxStackSize = 12; return method;
        }
        void WrapRaise()
        {
            var body = raise.Body; var il = body.GetILProcessor(); var first = body.Instructions[0];
            var scope = new VariableDefinition(module.ImportReference(typeof(RoutedDispatchScope))); body.Variables.Add(scope);
            var returned = new VariableDefinition(module.TypeSystem.Boolean); body.Variables.Add(returned);
            var end = Instruction.Create(OpCodes.Ret);
            foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                ret.OpCode = OpCodes.Ldc_I4_1; ret.Operand = null;
                var store = Instruction.Create(OpCodes.Stloc, returned); il.InsertAfter(ret, store); il.InsertAfter(store, Instruction.Create(OpCodes.Leave, end));
            }
            var cleanup = Instruction.Create(OpCodes.Ldloca, scope);
            foreach (var h in body.ExceptionHandlers) { h.TryEnd ??= cleanup; h.HandlerEnd ??= cleanup; }
            il.Append(cleanup);
            il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(RoutedDispatchScope).GetProperty(nameof(RoutedDispatchScope.IsEntered))!.GetMethod!)));
            var endFinally = Instruction.Create(OpCodes.Endfinally); il.Append(Instruction.Create(OpCodes.Brfalse, endFinally));
            il.Append(Instruction.Create(OpCodes.Ldloca, scope)); il.Append(Instruction.Create(OpCodes.Ldarg_2)); il.Append(Instruction.Create(OpCodes.Call, handled));
            il.Append(Instruction.Create(OpCodes.Ldloc, returned)); il.Append(Call(module, nameof(Probe.EndRoutedEvent))); il.Append(endFinally); il.Append(end);
            body.ExceptionHandlers.Add(new(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = end });
            var tail = setSource;
            foreach (var i in new[]
            {
                Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field),
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field), Instruction.Create(OpCodes.Call, name),
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field), Instruction.Create(OpCodes.Call, strategies),
                Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Call, handled), Call(module, nameof(Probe.BeginRoutedEvent)), Instruction.Create(OpCodes.Stloc, scope)
            }) { il.InsertAfter(tail, i); tail = i; }
            body.InitLocals = true; body.MaxStackSize += 8; MethodRewriter.ExpandBranches(raise);
        }
    }
    private static bool CallShape(Instruction call, int parameters, MetadataType result) =>
        call.Operand is MethodReference method && method.HasThis && method.Parameters.Count == parameters && method.ReturnType.MetadataType == result;
    private static T? UniqueOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        T? found = null;
        foreach (var value in source)
        {
            if (!predicate(value)) continue;
            if (found is not null) return null;
            found = value;
        }
        return found;
    }
    private static Instruction Call(ModuleDefinition module, string name) => Instruction.Create(OpCodes.Call, module.ImportReference(typeof(Probe).GetMethod(name)!));
}
