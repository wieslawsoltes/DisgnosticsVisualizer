using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

public static class WeaveEngine
{
    private const string Marker = "DiagnosticsVisualizer.Runtime.InstrumentedAttribute";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static WeaveManifest Weave(WeaveOptions options)
    {
        var directory = Path.GetFullPath(options.Directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var normalized = directory.Replace('\\', '/');
        if (normalized.Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/dotnet/shared/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to modify a package cache or shared runtime. Use application output copies.");
        var included = new HashSet<string>(options.Include, StringComparer.OrdinalIgnoreCase);
        var entryNames = Directory.EnumerateFiles(directory, "*.runtimeconfig.json").Select(path => Path.GetFileName(path)[..^".runtimeconfig.json".Length] + ".dll").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (options.EntryAssembly is not null) entryNames.Add(options.EntryAssembly);
        var paths = Directory.EnumerateFiles(directory, "*.dll").Where(path =>
        {
            var name = Path.GetFileName(path); return included.Contains(name) || entryNames.Contains(name) || name.StartsWith("Avalonia.", StringComparison.Ordinal);
        }).Order(StringComparer.Ordinal).ToArray();
        var staging = Path.Combine(directory, ".avtrace", "stage"); var originals = Path.Combine(directory, ".avtrace", "original");
        Directory.CreateDirectory(staging); Directory.CreateDirectory(originals);
        var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(directory); resolver.AddSearchDirectory(AppContext.BaseDirectory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var reports = new List<AssemblyReport>(); var pending = new List<(string Source, string Stage)>();
        var previousPath = Path.Combine(directory, "avtrace.manifest.json");
        var previous = File.Exists(previousPath) ? JsonSerializer.Deserialize<WeaveManifest>(File.ReadAllText(previousPath), Json) : null;
        try
        {
            foreach (var path in paths)
            {
                using (var pe = new PEReader(File.OpenRead(path)))
                {
                    if (pe.PEHeaders.CorHeader is null) continue;
                    if (pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size != 0)
                        throw new InvalidOperationException($"{Path.GetFileName(path)} contains ReadyToRun/native code. Instrument IL before native compilation.");
                }
                var pdb = Path.ChangeExtension(path, ".pdb");
                using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadSymbols = File.Exists(pdb) });
                if (assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == Marker) is { } existing)
                {
                    var version = existing.ConstructorArguments.Count == 1 ? existing.ConstructorArguments[0].Value as string : null;
                    if (version != HookCatalog.Version)
                        throw new InvalidOperationException($"{Path.GetFileName(path)} was instrumented with hook version {version ?? "unknown"}, not {HookCatalog.Version}. Restore or clean the output before upgrading diagnostics.");
                    var old = previous?.Assemblies.FirstOrDefault(a => a.File == Path.GetFileName(path)); if (old is not null) reports.Add(old); continue;
                }
                var module = assembly.MainModule; var originalMvid = module.Mvid.ToString();
                var signed = assembly.Name.HasPublicKey; var methodReports = new List<MethodReport>();
                var propertyReports = PropertyOperationRewriter.Instrument(module); methodReports.AddRange(propertyReports);
                var propertyTokens = propertyReports.Where(r => r.Status == "PropertyOperation").Select(r => r.Token).ToHashSet();
                var bindingReports = BindingOperationRewriter.Instrument(module); methodReports.AddRange(bindingReports);
                var bindingTokens = bindingReports.Where(r => r.Status == "BindingOperation").Select(r => r.Token).ToHashSet();
                var styleReports = StyleEvaluationRewriter.Instrument(module); methodReports.AddRange(styleReports);
                var styleTokens = styleReports.Where(r => r.Status == "StyleEvaluation").Select(r => r.Token).ToHashSet();
                var resourceReports = ResourceLookupRewriter.Instrument(module); methodReports.AddRange(resourceReports);
                var resourceTokens = resourceReports.Where(r => r.Status == "ResourceLookup").Select(r => r.Token).ToHashSet();
                methodReports.AddRange(RoutedEventRewriter.Instrument(module));
                if (options.Deep)
                    foreach (var method in AllTypes(module.Types).SelectMany(t => t.Methods).ToArray()) methodReports.AddRange(XamlSourceRewriter.Instrument(module, method));
                foreach (var kickoff in AllTypes(module.Types).SelectMany(t => t.Methods).ToArray())
                    if (HookCatalog.Match(kickoff, options.Deep) is { } subsystem && AsyncStateMachineRewriter.Instrument(module, kickoff, subsystem) is { } lifecycle) methodReports.Add(lifecycle);
                foreach (var type in AllTypes(module.Types))
                foreach (var method in type.Methods.ToArray())
                {
                    if (!method.HasBody || method.Name.StartsWith("__AvTrace", StringComparison.Ordinal) || propertyTokens.Contains(method.MetadataToken.ToInt32()) || bindingTokens.Contains(method.MetadataToken.ToInt32()) || resourceTokens.Contains(method.MetadataToken.ToInt32()) || styleTokens.Contains(method.MetadataToken.ToInt32())) continue;
                    var category = HookCatalog.Match(method, options.Deep); var notifications = MethodRewriter.InstrumentNotifications(module, method);
                    if (category is null && notifications > 0) category = Subsystem.Application; if (category is null) continue;
                    var sourceSite = SourceSiteBuilder.Create(module, method); var reason = MethodRewriter.Unsupported(method);
                    if (reason is null) MethodRewriter.Wrap(module, method, category.Value, SourceSiteSymbol.Encode(sourceSite));
                    methodReports.Add(new(method.MetadataToken.ToInt32(), method.FullName, category.ToString()!, reason is null ? "Instrumented" : notifications > 0 ? "NotificationsOnly" : "Skipped", notifications, reason, sourceSite));
                }
                methodReports.AddRange(DispatcherRewriter.Instrument(module));
                var bootstrapped = options.Bootstrap && module.EntryPoint is not null && TryBootstrap(module, directory, resolver);
                var changed = bootstrapped || methodReports.Any(r => r.Status != "Skipped"); var originalHash = Hash(path);
                if (!changed)
                { reports.Add(new(Path.GetFileName(path), originalHash, originalHash, originalMvid, originalMvid, assembly.Name.Version.ToString(), signed, false, methodReports.ToArray(), "No matching hooks")); continue; }
                if (signed && options.SigningKey is null && !options.AllowSignedRewrite)
                    throw new InvalidOperationException($"{Path.GetFileName(path)} has a signed identity. Supply its signing key or explicitly allow diagnostic-only signature invalidation with --allow-signed.");
                var marker = new CustomAttribute(module.ImportReference(typeof(InstrumentedAttribute).GetConstructor([typeof(string)])!));
                marker.ConstructorArguments.Add(new(module.TypeSystem.String, HookCatalog.Version)); assembly.CustomAttributes.Add(marker);
                if (signed && options.SigningKey is null) module.Attributes &= ~ModuleAttributes.StrongNameSigned;
                var destination = Path.Combine(staging, Path.GetFileName(path)); var writer = new WriterParameters { WriteSymbols = module.HasSymbols, DeterministicMvid = true };
                if (options.SigningKey is not null) writer.StrongNameKeyBlob = File.ReadAllBytes(options.SigningKey);
                assembly.Write(destination, writer);
                using var verification = AssemblyDefinition.ReadAssembly(destination, new ReaderParameters { AssemblyResolver = resolver });
                if (!verification.CustomAttributes.Any(a => a.AttributeType.FullName == Marker)) throw new InvalidDataException("Post-write validation failed.");
                reports.Add(new(Path.GetFileName(path), originalHash, Hash(destination), originalMvid, verification.MainModule.Mvid.ToString(), assembly.Name.Version.ToString(), signed, bootstrapped,
                    methodReports.ToArray(), signed && options.SigningKey is null ? "Instrumented; diagnostic signature invalidation" : "Instrumented")); pending.Add((path, destination));
            }
            foreach (var (source, stage) in pending)
            {
                File.Copy(source, Path.Combine(originals, Path.GetFileName(source)), true);
                var sourcePdb = Path.ChangeExtension(source, ".pdb"); var stagePdb = Path.ChangeExtension(stage, ".pdb");
                if (File.Exists(sourcePdb)) File.Copy(sourcePdb, Path.Combine(originals, Path.GetFileName(sourcePdb)), true);
                File.Move(stage, source, true); if (File.Exists(stagePdb)) File.Move(stagePdb, sourcePdb, true);
            }
            var manifest = new WeaveManifest("avtrace.weaving/1", options.Deep ? "deep" : "balanced", reports.ToArray());
            File.WriteAllText(previousPath + ".tmp", JsonSerializer.Serialize(manifest, Json)); File.Move(previousPath + ".tmp", previousPath, true); return manifest;
        }
        finally { resolver.Dispose(); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static bool TryBootstrap(ModuleDefinition module, string directory, IAssemblyResolver resolver)
    {
        var path = Path.Combine(directory, "DiagnosticsVisualizer.Agent.Avalonia.dll"); if (!File.Exists(path)) return false;
        using var agent = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
        var init = agent.MainModule.Types.Single(t => t.FullName == "DiagnosticsVisualizer.Agent.Avalonia.DiagnosticsHost").Methods.Single(m => m.Name == "InitializeFromEnvironment" && m.Parameters.Count == 0);
        var type = module.Types.Single(t => t.Name == "<Module>"); var constructor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (constructor is null)
        { constructor = new MethodDefinition(".cctor", MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void); constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(constructor); }
        constructor.Body.GetILProcessor().InsertBefore(constructor.Body.Instructions[0], Instruction.Create(OpCodes.Call, module.ImportReference(init))); return true;
    }
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    { foreach (var type in types) { yield return type; foreach (var nested in AllTypes(type.NestedTypes)) yield return nested; } }
    public static void Restore(string directory)
    {
        directory = Path.GetFullPath(directory); var manifestPath = Path.Combine(directory, "avtrace.manifest.json"); if (!File.Exists(manifestPath)) return;
        var manifest = JsonSerializer.Deserialize<WeaveManifest>(File.ReadAllText(manifestPath), Json)!; var pending = new List<(string Original, string Target)>();
        foreach (var assembly in manifest.Assemblies)
        {
            if (Path.GetFileName(assembly.File) != assembly.File) throw new InvalidDataException("Invalid manifest path.");
            var target = Path.Combine(directory, assembly.File); var original = Path.Combine(directory, ".avtrace", "original", assembly.File); if (!File.Exists(original)) continue;
            if (Hash(original) != assembly.OriginalSha256) throw new InvalidDataException("Original assembly checksum mismatch.");
            var current = File.Exists(target) ? Hash(target) : ""; if (current == assembly.OriginalSha256) continue;
            if (current != assembly.OutputSha256) throw new InvalidDataException("Application output changed since instrumentation; refusing to overwrite it."); pending.Add((original, target));
        }
        foreach (var (original, target) in pending)
        { ReplaceFromCopy(original, target); var pdb = Path.ChangeExtension(original, ".pdb"); if (File.Exists(pdb)) ReplaceFromCopy(pdb, Path.ChangeExtension(target, ".pdb")); }
        File.Delete(manifestPath);
    }
    private static void ReplaceFromCopy(string original, string target)
    {
        var temporary = target + ".avtrace-restore-" + Guid.NewGuid().ToString("N");
        try { File.Copy(original, temporary); File.Move(temporary, target, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
