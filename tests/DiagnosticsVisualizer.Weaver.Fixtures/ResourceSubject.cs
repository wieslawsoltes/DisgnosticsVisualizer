using Avalonia.Controls;
using Avalonia.Styling;

namespace DiagnosticsVisualizer.Weaver.Fixtures;

public static class ResourceSubject
{
    private sealed class OpaqueKey { public override string ToString() => throw new InvalidOperationException("Do not stringify keys"); }
    public static string Exercise()
    {
        var resources = new ResourceDictionary(); var lower = new ResourceDictionary { ["Shared"] = "lower" };
        var higher = new ResourceDictionary { ["Shared"] = "higher" }; resources.MergedDictionaries.Add(lower); resources.MergedDictionaries.Add(higher);
        var builds = 0; var opaque = new OpaqueKey(); resources[opaque] = "opaque-result";
        resources.AddDeferred("Lazy", _ => { builds++; return new object(); });
        resources.AddDeferred("Throwing", _ => throw new InvalidOperationException("Original deferred failure"));
        resources["Null"] = null;
        resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["Themed"] = "dark" };
        if (!resources.TryGetResource("Shared", null, out var shared) || !Equals("higher", shared)) throw new InvalidOperationException("Merged precedence changed");
        if (!resources.TryGetResource("Themed", ThemeVariant.Dark, out var themed) || !Equals("dark", themed)) throw new InvalidOperationException("Theme changed");
        if (resources.TryGetResource("Missing", null, out _) || !resources.TryGetResource("Null", null, out var nil) || nil is not null) throw new InvalidOperationException("Missing/null changed");
        if (!resources.TryGetResource(opaque, null, out _)) throw new InvalidOperationException("Opaque key changed");
        if (!resources.TryGetResource("Lazy", null, out var first) || !resources.TryGetResource("Lazy", null, out var second) || !ReferenceEquals(first, second) || builds != 1)
            throw new InvalidOperationException("Deferred evaluation changed");
        try { resources.TryGetResource("Throwing", null, out _); throw new Exception("Swallowed deferred failure"); }
        catch (InvalidOperationException error) when (error.Message == "Original deferred failure") { }
        return "RESOURCE_LOOKUP_OK merged=higher theme=dark lazy=1 null=found missing=false opaque=safe fault=preserved";
    }
}
