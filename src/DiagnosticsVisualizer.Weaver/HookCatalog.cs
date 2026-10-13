using DiagnosticsVisualizer.Core;
using Mono.Cecil;

namespace DiagnosticsVisualizer.Weaver;

/// <summary>Version-adaptive named hooks. The output manifest records actual matches, not assumed coverage.</summary>
public static class HookCatalog
{
    public const string Version = "11";
    public static Subsystem? Match(MethodDefinition method, bool deep)
    {
        var attribute = method.CustomAttributes.Concat(method.DeclaringType.CustomAttributes).FirstOrDefault(a => a.AttributeType.FullName == "DiagnosticsVisualizer.Runtime.TraceAttribute");
        if (attribute is not null && attribute.ConstructorArguments.Count > 0) return (Subsystem)Convert.ToInt32(attribute.ConstructorArguments[0].Value);
        var t = method.DeclaringType.FullName; var n = method.Name;
        if (n.Contains("XamlIlPopulate", StringComparison.Ordinal) || n.Contains("XamlIlBuild", StringComparison.Ordinal) || n.Contains("XamlIlTrampoline", StringComparison.Ordinal)) return Subsystem.Xaml;
        if (deep && method.IsVirtual && n is "MeasureOverride" or "ArrangeOverride" && DerivesFromLayoutable(method.DeclaringType)) return Subsystem.Layout;
        if (!t.StartsWith("Avalonia.", StringComparison.Ordinal))
        {
            if (deep && method.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")) return Subsystem.Application;
            if (n is "OnPropertyChanged" or "RaisePropertyChanged" or "RaiseAndSetIfChanged") return Subsystem.PropertyNotifications;
            if (n is "OnCollectionChanged" or "RaiseCollectionChanged") return Subsystem.CollectionNotifications;
            return null;
        }
        if (t.Contains("DataTemplate", StringComparison.Ordinal) && n is "Build" or "Match" or "BuildWithDataType") return Subsystem.DataTemplates;
        if (t.Contains("ControlTheme", StringComparison.Ordinal) && !method.IsSpecialName) return Subsystem.ControlThemes;
        if (t.Contains("Selector", StringComparison.Ordinal) && n is "Match" or "Evaluate" or "EvaluateCore" or "MatchCore") return Subsystem.Selectors;
        if ((t.Contains("Resource", StringComparison.Ordinal) || t.EndsWith("StyledElement", StringComparison.Ordinal)) && n is "TryGetResource" or "GetResource" or "OnResourcesChanged" or "NotifyHostedResourcesChanged" or "ResourcesChanged") return Subsystem.Resources;
        if (t.Contains("DynamicResource", StringComparison.Ordinal) && n is "ProvideValue" or "Initiate" or "OnNext" or "StartCore") return Subsystem.Resources;
        if (t.Contains("StaticResource", StringComparison.Ordinal) && n == "ProvideValue") return Subsystem.Resources;
        if (t.Contains("Xaml", StringComparison.Ordinal) && n is "Load" or "LoadCore" or "Populate" or "Build") return Subsystem.Xaml;
        if (t.Contains("Layout", StringComparison.Ordinal) && n is "Measure" or "Arrange" or "MeasureCore" or "ArrangeCore" or "InvalidateMeasure" or "InvalidateArrange" or "ExecuteLayoutPass" or "ExecuteLayoutPassCore" or "ExecuteMeasurePass" or "ExecuteArrangePass") return Subsystem.Layout;
        if (n is "ApplyTemplate" or "OnTemplateApplied" or "OnTemplateChanged") return Subsystem.Templates;
        if (n is "ApplyStyling" or "InvalidateStyles" or "StyleApplied" || (t.Contains("Styling", StringComparison.Ordinal) && n is "TryAttach" or "Attach" or "AttachTo" or "Detach" or "Activate" or "Deactivate")) return Subsystem.Styles;
        if (t.Contains("Threading", StringComparison.Ordinal) && n is "Post" or "Invoke" or "InvokeAsync" or "InvokeCore" or "Execute" or "RunJobs" or "RunJobsCore" or "Enqueue" or "AddToQueue" or "Send" or "ExecuteJob") return Subsystem.Dispatcher;
        if ((t.Contains("Interactive", StringComparison.Ordinal) || t.Contains("EventRoute", StringComparison.Ordinal) || t.Contains("RoutedEvent", StringComparison.Ordinal)) && n is "RaiseEvent" or "RaiseEventImpl" or "Invoke" or "InvokeHandlers") return Subsystem.RoutedEvents;
        if (n is "OnAttachedToVisualTree" or "OnDetachedFromVisualTree" or "OnVisualParentChanged" or "VisualChildrenChanged") return Subsystem.VisualTree;
        if (n is "OnAttachedToLogicalTree" or "OnDetachedFromLogicalTree" or "OnLogicalParentChanged" or "LogicalChildrenChanged") return Subsystem.LogicalTree;
        if ((t.Contains("AvaloniaObject", StringComparison.Ordinal) || t.Contains("ValueStore", StringComparison.Ordinal)) && (n is "SetValue" or "SetCurrentValue" or "ClearValue" or "RaisePropertyChanged" or "SetAndRaise" or "ReevaluateEffectiveValue" || deep && n == "GetValue")) return Subsystem.Properties;
        if ((t.Contains("Binding", StringComparison.Ordinal) || t.Contains("ExpressionObserver", StringComparison.Ordinal)) && n is "OnNext" or "PublishValue" or "SetValue" or "WriteValueToSource" or "UpdateSource" or "UpdateTarget" or "StartCore" or "OnSourceChanged") return Subsystem.Bindings;
        if (t.Contains("Animation", StringComparison.Ordinal) && n is "Apply" or "RunAsync" or "Step" or "Pulse" or "OnNext" or "Interpolate" or "Evaluate") return Subsystem.Animation;
        if (t.Contains("Composition", StringComparison.Ordinal) && n is "Commit" or "CommitCore" or "Render" or "SerializeChanges" or "DeserializeChanges" or "ProcessBatch" or "Update") return Subsystem.Compositor;
        if ((t.Contains("Rendering", StringComparison.Ordinal) || t.Contains("Skia", StringComparison.Ordinal)) && n is "Render" or "Update" or "UpdateCore" or "Paint" or "Draw" or "RenderCore" or "Invalidate" or "AddDirty" or "CreateDrawingContext") return Subsystem.Rendering;
        return null;
    }
    private static bool DerivesFromLayoutable(TypeDefinition type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (TypeReference? current = type; current is not null && seen.Add(current.FullName);)
        {
            if (current.FullName == "Avalonia.Layout.Layoutable") return true;
            try { current = current.Resolve()?.BaseType; } catch (AssemblyResolutionException) { return false; }
        }
        return false;
    }
}
