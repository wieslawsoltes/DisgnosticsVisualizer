using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DiagnosticsVisualizer.Sample;

public sealed partial class WorkloadWindow
{
    private sealed class ResourceKey
    {
        public override string ToString() => throw new InvalidOperationException("Resource keys must not be formatted by diagnostics.");
    }

    // Real application lookups, never calls into diagnostics. Used by both packaged capture runs.
    private void ExerciseResourceLookups(Button button)
    {
        var lower = new ResourceDictionary { ["DvLookupShared"] = new object() };
        var winningValue = new object();
        var higher = new ResourceDictionary { ["DvLookupShared"] = winningValue, ["DvLookupNull"] = null };
        Resources.MergedDictionaries.Add(lower);
        Resources.MergedDictionaries.Add(higher);
        var dark = new object();
        var themed = (ResourceDictionary)Resources.ThemeDictionaries[ThemeVariant.Dark];
        themed["DvLookupTheme"] = dark;
        var builds = 0;
        higher.AddDeferred("DvLookupDeferred", _ => { builds++; return new object(); });
        higher.AddDeferred("DvLookupThrows", _ => throw new InvalidOperationException("Expected resource construction failure"));
        var key = new ResourceKey(); higher[key] = winningValue;

        if (!button.TryFindResource("DvLookupShared", ThemeVariant.Dark, out var shared) || !ReferenceEquals(shared, winningValue))
            throw new InvalidOperationException("Logical-tree or merged dictionary resolution changed.");
        if (!button.TryFindResource("DvLookupTheme", ThemeVariant.Dark, out var theme) || !ReferenceEquals(theme, dark))
            throw new InvalidOperationException("Requested theme resolution changed.");
        if (!higher.TryGetResource("DvLookupNull", null, out var nil) || nil is not null || higher.TryGetResource("DvLookupMissing", null, out _))
            throw new InvalidOperationException("A found null was confused with a miss.");
        if (!higher.TryGetResource(key, null, out var opaque) || !ReferenceEquals(opaque, winningValue))
            throw new InvalidOperationException("Opaque key semantics changed.");
        if (!higher.TryGetResource("DvLookupDeferred", null, out var first) ||
            !higher.TryGetResource("DvLookupDeferred", null, out var second) || !ReferenceEquals(first, second) || builds != 1)
            throw new InvalidOperationException("Deferred evaluation was replayed or replaced.");
        try
        {
            higher.TryGetResource("DvLookupThrows", null, out _);
            throw new InvalidOperationException("Deferred exception was swallowed.");
        }
        catch (InvalidOperationException error) when (error.Message == "Expected resource construction failure") { }
        Console.WriteLine("RESOURCE_LOOKUP_WORKLOAD_OK merged=higher theme=dark lazy=1 null=found missing=false opaque=safe fault=preserved");
    }
}
