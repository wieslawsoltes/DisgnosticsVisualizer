using System.Text.Json;
using DiagnosticsVisualizer.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiagnosticsVisualizer.Weaver;

internal static class SourceSiteBuilder
{
    public static SourceSite Create(ModuleDefinition module, MethodDefinition method)
    {
        // Async kickoff methods often have no points: consult the generated MoveNext PDB.
        var asyncType = method.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")?
            .ConstructorArguments.FirstOrDefault().Value as TypeReference;
        var points = method.DebugInformation.SequencePoints;
        if (asyncType?.Resolve()?.Methods.FirstOrDefault(m => m.Name == "MoveNext") is { } move && points.Count == 0)
            points = move.DebugInformation.SequencePoints;
        var point = points.FirstOrDefault(p => !p.IsHidden && p.StartLine > 0);
        var document = point?.Document;
        var algorithm = document?.HashAlgorithm switch { DocumentHashAlgorithm.SHA1 => "SHA1", DocumentHashAlgorithm.SHA256 => "SHA256", _ => "" };
        var checksum = algorithm.Length == 0 ? "" : Convert.ToHexString(document!.Hash);
        if (checksum.Length != (algorithm == "SHA1" ? 40 : 64)) { algorithm = ""; checksum = ""; }
        // Never embed a developer's home directory or source text into a shared capture.
        var name = document?.Url.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        return new(module.Assembly.Name.Name, module.Mvid, method.MetadataToken.ToInt32(), method.FullName,
            name, point?.StartLine ?? 0, point?.StartColumn ?? 0, point?.EndLine ?? 0, point?.EndColumn ?? 0,
            algorithm, checksum, SourceLink(module, document?.Url), asyncType is not null);
    }

    private static string SourceLink(ModuleDefinition module, string? document)
    {
        if (document is null) return "";
        var content = module.CustomDebugInformations.OfType<SourceLinkDebugInformation>().FirstOrDefault()?.Content;
        if (content is null || content.Length > 1_048_576) return "";
        try
        {
            using var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            if (!json.RootElement.TryGetProperty("documents", out var mappings)) return "";
            foreach (var mapping in mappings.EnumerateObject().OrderByDescending(p => p.Name.Length))
            {
                if (mapping.Value.ValueKind != JsonValueKind.String) continue;
                var pattern = mapping.Name; var target = mapping.Value.GetString()!;
                var star = pattern.IndexOf('*'); string suffix;
                if (star < 0) { if (pattern != document) continue; suffix = ""; }
                else
                {
                    if (star != pattern.Length - 1 || !document.StartsWith(pattern[..star], StringComparison.Ordinal)) continue;
                    suffix = document[star..].Replace('\\', '/');
                    if (suffix.Split('/').Any(p => p is ".." or ".")) continue;
                }
                var url = star < 0 ? target : target.Replace("*", string.Join('/', suffix.Split('/').Select(Uri.EscapeDataString)), StringComparison.Ordinal);
                if (url.Length <= 4096 && SourceSiteSymbol.IsSafeSourceUrl(url)) return url;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { }
        return "";
    }
}
