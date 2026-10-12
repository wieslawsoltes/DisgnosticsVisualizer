using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>
/// Uses existing compiler sequence points, never guesses from an XML parser or executes XAML.
/// Sites are collected before rewriting; deferred factories are instrumented but never executed by inspection.
/// </summary>
internal static class XamlSourceRewriter
{
    public static IEnumerable<MethodReport> Instrument(ModuleDefinition module, MethodDefinition method)
    {
        if (!method.HasBody) yield break;
        var points = method.DebugInformation.SequencePoints.OrderBy(p => p.Offset).ToArray();
        if (!points.Any(IsXaml)) yield break;
        var original = method.Body.Instructions.ToArray();
        var insertions = new List<(Instruction Anchor, SourceSite Site)>();
        SequencePoint? current = null; var pointIndex = 0;
        foreach (var instruction in original)
        {
            while (pointIndex < points.Length && points[pointIndex].Offset <= instruction.Offset)
                current = points[pointIndex++];
            if (current is null || !IsXaml(current) || instruction.OpCode != OpCodes.Newobj ||
                instruction.Operand is not MethodReference ctor || !CanObserve(ctor.DeclaringType)) continue;
            var site = SourceSiteBuilder.CreatePoint(module, method, current) with
            { IlOffset = instruction.Offset, Mapping = SourceMappingKind.XamlConstruction };
            insertions.Add((instruction, site));
        }
        var il = method.Body.GetILProcessor();
        var hook = module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.XamlObject))!);
        foreach (var (anchor, site) in insertions)
        {
            // Preserve every pre-existing stack value, including the constructed object.
            var tail = anchor;
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldstr, SourceSiteSymbol.Encode(site)),
                Instruction.Create(OpCodes.Ldc_I4, (int)site.Mapping), Instruction.Create(OpCodes.Call, hook) })
            { il.InsertAfter(tail, instruction); tail = instruction; }
            yield return new(method.MetadataToken.ToInt32(), method.FullName, "Xaml", "XamlSource", 0,
                "Successful newobj at original IL offset; location is the compiler sequence point.", site);
        }
        var rootPoint = points.FirstOrDefault(IsXaml);
        if (rootPoint is not null && method.IsStatic && method.Name.Contains("XamlIlPopulate", StringComparison.Ordinal) &&
            method.Parameters.Count == 2 && !method.Parameters[1].ParameterType.IsValueType &&
            !method.Parameters[1].ParameterType.IsByReference && !method.Parameters[1].ParameterType.IsGenericParameter)
        {
            var site = SourceSiteBuilder.CreatePoint(module, method, rootPoint) with
            { IlOffset = original[0].Offset, Mapping = SourceMappingKind.XamlRoot };
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg, method.Parameters[1]),
                Instruction.Create(OpCodes.Ldstr, SourceSiteSymbol.Encode(site)), Instruction.Create(OpCodes.Ldc_I4, (int)site.Mapping),
                Instruction.Create(OpCodes.Call, hook) }) il.InsertBefore(original[0], instruction);
            yield return new(method.MetadataToken.ToInt32(), method.FullName, "Xaml", "XamlSource", 0,
                "Compiled root population; this is not a constructor allocation.", site);
        }
        if (insertions.Count > 0 || rootPoint is not null)
        { MethodRewriter.ExpandBranches(method); method.Body.MaxStackSize += 4; }
    }

    private static bool IsXaml(SequencePoint p) => !p.IsHidden && p.StartLine > 0 &&
        (p.Document.Url.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || p.Document.Url.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
    private static bool CanObserve(TypeReference type)
    {
        if (type.IsValueType || type.IsGenericParameter || (type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal)) ||
            type.Namespace == "CompiledAvaloniaXaml") return false;
        // The CLR must receive a reference, never a value type or byref-like object.
        try { return type.Resolve() is { IsValueType: false } resolved && !resolved.Name.StartsWith("XamlClosure", StringComparison.Ordinal); }
        catch (AssemblyResolutionException) { return false; }
    }
}
