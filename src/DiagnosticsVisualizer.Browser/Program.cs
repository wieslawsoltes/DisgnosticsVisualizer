using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Workbench;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace DiagnosticsVisualizer.Browser;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static Task Main(string[] args) => AppBuilder.Configure<DiagnosticsVisualizer.App.App>()
        .WithInterFont().StartBrowserAppAsync("out");
    /// <summary>Read-only UI health, without target values or capture credentials.</summary>
    [JSExport]
    public static string GetViewerHealth()
    {
        var view = (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView as WorkbenchView;
        var model = view?.Model;
        var visuals = view?.GetVisualDescendants().ToArray() ?? [];
        var commandSearch = visuals.OfType<TextBox>().FirstOrDefault(text => text.Watermark == "Type a command…" && text.IsEffectivelyVisible);
        var commandSelection = visuals.OfType<ListBox>().Where(list => list.IsEffectivelyVisible)
            .Select(list => list.SelectedItem as string).FirstOrDefault(text => text?.StartsWith("Show ", StringComparison.Ordinal) == true);
        var root = visuals.OfType<DockControl>().FirstOrDefault()?.Layout;
        var activeDocuments = Traverse(root).OfType<IDocumentDock>().Select(dock => dock.ActiveDockable?.Id ?? "").ToArray();
        return JsonSerializer.Serialize(new
        {
            ready = view is not null, events = model?.Trace?.Events.Length ?? 0,
            nodes = model?.Trace?.Snapshot.Nodes.Length ?? 0,
            findings = model?.Analysis?.Findings.Length ?? 0, visualCount = visuals.Length,
            commandPaletteOpen = commandSearch is not null, commandQuery = commandSearch?.Text ?? "",
            commandSelection = commandSelection ?? "", activeDocuments,
            causalGraphVisible = visuals.OfType<CausalityGraph>().Any(graph => graph.IsEffectivelyVisible),
            selectedEvent = model?.SelectedEventId.ToString() ?? "0", selectedObject = model?.SelectedObjectId.ToString() ?? "0",
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
