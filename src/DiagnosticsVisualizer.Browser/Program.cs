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

namespace DiagnosticsVisualizer.Browser;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static Task Main(string[] args) => AppBuilder.Configure<DiagnosticsVisualizer.App.App>()
        .WithInterFont().StartBrowserAppAsync("out");

    /// <summary>Read-only test/embedding health; never exposes capture credentials or target values.</summary>
    [JSExport]
    public static string GetViewerHealth()
    {
        var view = (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView as WorkbenchView;
        var model = view?.Model;
        var visuals = view?.GetVisualDescendants().ToArray() ?? [];
        return JsonSerializer.Serialize(new
        {
            ready = view is not null,
            events = model?.Trace?.Events.Length ?? 0,
            nodes = model?.Trace?.Snapshot.Nodes.Length ?? 0,
            findings = model?.Analysis?.Findings.Length ?? 0,
            visualCount = visuals.Length,
            commandPaletteOpen = visuals.OfType<TextBox>().Any(text => text.Watermark == "Type a command…" && text.IsEffectivelyVisible),
            causalGraphVisible = visuals.OfType<CausalityGraph>().Any(graph => graph.IsEffectivelyVisible),
            selectedEvent = model?.SelectedEventId.ToString() ?? "0",
            selectedObject = model?.SelectedObjectId.ToString() ?? "0",
            source = model?.Source ?? "Starting",
            error = model?.Error ?? ""
        });
    }
}
