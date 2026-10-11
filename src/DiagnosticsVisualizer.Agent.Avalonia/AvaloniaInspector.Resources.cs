using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Agent.Avalonia;

public sealed partial class AvaloniaInspector
{
    // Candidate inventory, deliberately NOT a lookup evaluator. Never call TryGetResource:
    // doing so may build deferred templates, load resources, or execute application factories.
    private void InspectResourceCandidates(StyledElement selected, long selectedId,
        List<PropertySnapshot> properties, List<RelationSnapshot>? relations)
    {
        const int maxProviders = 64, maxEntries = 512, maxScopes = 32;
        var providers = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var scopes = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(object Provider, long Owner, string Kind, string Scope)>();
        var entries = 0; var limited = false;
        void Relation(long source, object target, string kind, string detail)
        {
            if (relations is null) return;
            if (relations.Count >= TraceLimits.MaxRelationsPerSnapshot) { limited = true; return; }
            var id = _session.GetObjectId(target);
            if (source > 0 && id > 0) relations.Add(new(source, id, kind, ValueRedactor.Limit(detail)));
        }
        void Enqueue(object provider, long owner, string kind, string scope)
        {
            Relation(owner, provider, kind, scope);
            if (!providers.Add(provider)) return;
            if (providers.Count > maxProviders) { limited = true; return; }
            pending.Enqueue((provider, owner, kind, scope));
        }
        void Scope(StyledElement element, string label)
        {
            var owner = _session.GetObjectId(element);
            if (!ReferenceEquals(element, selected)) Relation(selectedId, element, "Resource scope", label);
            Enqueue(element.Resources, owner, "Resources", label);
            foreach (var style in element.Styles.Take(128))
                if (style is Style concrete)
                { Relation(owner, concrete, "Style", label); Enqueue(concrete.Resources, _session.GetObjectId(concrete), "Style resources", label); }
            if (element is Control { Theme: { } theme })
                { Relation(owner, theme, "ControlTheme", label); Enqueue(theme.Resources, _session.GetObjectId(theme), "Theme resources", label); }
        }
        try
        {
            ILogical? current = selected;
            while (current is not null && scopes.Add(current))
            {
                if (scopes.Count > maxScopes) { limited = true; break; }
                if (current is StyledElement element) Scope(element, ReferenceEquals(element, selected) ? "Local" : $"Ancestor {scopes.Count - 1}");
                current = current.LogicalParent;
            }
            if (Application.Current is { } application)
                { Relation(selectedId, application, "Resource scope", "Application"); Enqueue(application.Resources, _session.GetObjectId(application), "Resources", "Application"); }
            while (pending.TryDequeue(out var next))
            {
                // Only the framework dictionary's raw enumerator is inspected. Unknown resource
                // providers stay graph nodes; no user-defined enumerator or lookup is invoked.
                if (next.Provider.GetType() != typeof(ResourceDictionary)) continue;
                var dictionary = (ResourceDictionary)next.Provider;
                var dictionaryId = _session.GetObjectId(dictionary);
                foreach (var resource in dictionary)
                {
                    if (++entries > maxEntries || properties.Count >= TraceLimits.MaxPropertiesPerSnapshot - 1)
                    { limited = true; break; }
                    var key = resource.Key is string text ? ValueRedactor.Limit(text, 256) : $"<{resource.Key.GetType().Name}>";
                    properties.Add(new(selectedId, key, "Resource candidate", _redactor.Format(key, resource.Value),
                        "Candidate only", $"{next.Scope} · dictionary #{dictionaryId}"));
                    if (resource.Value is { } value) Relation(dictionaryId, value, "Resource value", key);
                }
                if (limited && entries > maxEntries) break;
                foreach (var merged in dictionary.MergedDictionaries.Take(maxProviders + 1))
                    Enqueue(merged, dictionaryId, "Merged dictionary", next.Scope + " / merged");
                foreach (var themed in dictionary.ThemeDictionaries.Take(maxProviders + 1))
                {
                    var variant = ReferenceEquals(themed.Key, ThemeVariant.Dark) ? "Dark" :
                        ReferenceEquals(themed.Key, ThemeVariant.Light) ? "Light" :
                        ReferenceEquals(themed.Key, ThemeVariant.Default) ? "Default" : "Custom variant";
                    Enqueue(themed.Value, dictionaryId, "Theme dictionary", ValueRedactor.Limit(next.Scope + " / " + variant, 256));
                }
            }
        }
        catch (Exception error)
        {
            properties.Add(new(selectedId, "Resource inventory", "Capability", "Unavailable", "Candidate only", error.GetType().Name));
            limited = true;
        }
        if (limited)
        {
            if (properties.Count >= TraceLimits.MaxPropertiesPerSnapshot) properties.RemoveAt(properties.Count - 1);
            properties.Add(new(selectedId, "Resource inventory", "Inspection limit", "Partial", "Candidate only",
                $"Bounded to {maxScopes} logical scopes, {maxProviders} providers and {maxEntries} entries; missing candidates are unknown."));
        }
    }
}
