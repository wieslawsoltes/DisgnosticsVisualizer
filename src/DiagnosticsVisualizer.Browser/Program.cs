using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Styling;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace DiagnosticsVisualizer.Browser;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static Task Main(string[] args)
    {
        WorkspaceServices.Storage = new BrowserWorkspaceStorage();
        return AppBuilder.Configure<DiagnosticsVisualizer.App.App>().WithInterFont().StartBrowserAppAsync("out");
    }
    [JSImport("readWorkspace", "diagnosticsStorage")]
    internal static partial string? ReadWorkspace();
    [JSImport("writeWorkspace", "diagnosticsStorage")]
    internal static partial void WriteWorkspace(string json);
    private sealed class BrowserWorkspaceStorage : IWorkspaceStorage
    {
        public string? Read() => ReadWorkspace();
        public void Write(string json) => WriteWorkspace(json);
    }
    /// <summary>Read-only UI health, without target values or capture credentials.</summary>
    [JSExport]
    public static string GetViewerHealth()
    {
        var view = (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView as WorkbenchView;
        var model = view?.Model;
        var visuals = view?.GetVisualDescendants().ToArray() ?? [];
        var commandSearch = visuals.OfType<TextBox>().FirstOrDefault(text => text.Watermark == "Type a command…" && text.IsEffectivelyVisible);
        var commandSelection = visuals.OfType<ListBox>().Where(list => list.IsEffectivelyVisible)
            .Select(list => list.SelectedItem as string).FirstOrDefault(text => text is not null && (text.StartsWith("Show ", StringComparison.Ordinal) || text.StartsWith("Workspace:", StringComparison.Ordinal)));
        var root = visuals.OfType<DockControl>().FirstOrDefault()?.Layout;
        var activeDocuments = Traverse(root).OfType<IDocumentDock>().Select(dock => dock.ActiveDockable?.Id ?? "").ToArray();
        var overview = visuals.OfType<ActivityOverview>().FirstOrDefault();
        var overviewPosition = overview?.TranslatePoint(default, view!) ?? default;
        Application.Current!.Resources.TryGetResource("DockSurfaceWorkbenchBrush", ThemeVariant.Dark, out var surface);
        var eventGrid = visuals.OfType<DataGrid>().FirstOrDefault(grid => grid.Name == "EventsGrid");
        return JsonSerializer.Serialize(new
        {
            ready = view is not null, events = model?.Trace?.Events.Length ?? 0,
            nodes = model?.Trace?.Snapshot.Nodes.Length ?? 0,
            findings = model?.Analysis?.Findings.Length ?? 0, visualCount = visuals.Length,
            commandPaletteOpen = commandSearch is not null, commandInputFocused = commandSearch?.IsFocused == true, commandQuery = commandSearch?.Text ?? "",
            commandSelection = commandSelection ?? "", activeDocuments,
            causalGraphVisible = visuals.OfType<CausalityGraph>().Any(graph => graph.IsEffectivelyVisible),
            selectedEvent = model?.SelectedEventId.ToString() ?? "0", selectedObject = model?.SelectedObjectId.ToString() ?? "0",
            theme = view?.ThemeName, dockSurface = (surface as ISolidColorBrush)?.Color.ToString(),
            overview = new { x = overviewPosition.X, y = overviewPosition.Y, width = overview?.Bounds.Width ?? 0, height = overview?.Bounds.Height ?? 0 },
            statisticsVisible = visuals.OfType<DataGrid>().Any(grid => grid.Name == "StatisticsGrid" && grid.IsEffectivelyVisible),
            sceneVisible = visuals.OfType<LayoutSceneControl>().Any(scene => scene.IsEffectivelyVisible),
            filteredEvents = eventGrid?.ItemsSource?.Cast<object>().Count() ?? 0,
            eventQuery = visuals.OfType<TextBox>().FirstOrDefault(text => text.Watermark?.StartsWith("Query:", StringComparison.Ordinal) == true)?.Text,
            hasRange = model?.Range is not null, workspacePreset = view?.WorkspacePresetName, workspaceError = view?.WorkspaceError,
            profileOperations = model?.Profile?.Operations.Length ?? 0, checkpoints = model?.Trace?.Checkpoints.Length ?? 0,
            rangeEvents = model?.Profile?.Events ?? 0, queryError = model?.QueryError,
            source = model?.Source ?? "Starting", error = model?.Error ?? ""
        });
    }
    private static IEnumerable<IDockable> Traverse(IDockable? root)
    {
        if (root is null) yield break;
        var pending = new Stack<IDockable>(); pending.Push(root);
        var seen = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            var node = pending.Pop(); if (!seen.Add(node)) continue;
            yield return node;
            if (node is IDock dock)
                foreach (var child in dock.VisibleDockables ?? []) pending.Push(child);
        }
    }
}
