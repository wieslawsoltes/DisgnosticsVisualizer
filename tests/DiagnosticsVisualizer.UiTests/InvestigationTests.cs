using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class InvestigationTests
{
    [AvaloniaFact] public async Task ObjectOriginsNavigateToSourceAndInvestigationLayoutRestores()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show();
        var bytes = Encoding.UTF8.GetBytes("<Window>\n  <Button />\n</Window>");
        var site = new SourceSite("TestFixture", Guid.NewGuid(), 0x06000001, "Widget.XamlIlPopulate", "Widget.axaml", 2, 3, 2, 13,
            "SHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), "", false)
            { IlOffset = 12, Mapping = SourceMappingKind.XamlConstruction };
        using var input = new MemoryStream();
        await TraceFile.WriteAsync(input, new TraceBlock
        {
            SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000,
            Symbols = [new(1, SourceSiteSymbol.Encode(site)), new(2, "Dispatcher queue test fixture")],
            Objects = [new(5, "Button", "WidgetButton"), new(50, "DispatcherOperation", "Test callback")],
            Events = [new(1, 0, 1, 0, 5, 0, 1, 1, Subsystem.Xaml, TraceEventKind.Link, XamlObjectEvent.ConstructionCode),
                new(10, 0, 5, 0, 50, 0, 2, 2, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.QueuedCode, DispatcherEvent.PackPriority(-6, false)),
                new(11, 10, 20, 0, 50, 0, 1, 2, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.PriorityCode, DispatcherEvent.PackPriority(2, true)),
                new(12, 10, 30, 0, 50, 0, 1, 2, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.StartedCode, DispatcherEvent.PackPriority(2, true)),
                new(13, 10, 35, 0, 50, 0, 1, 2, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.FinishedCode, (long)DispatcherOutcome.Returned)]
        });
        input.Position = 0; await view.Model.ImportAsync(input, "Deterministic investigation test fixture");
        view.ApplyWorkspacePreset(WorkspacePreset.Investigate); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.GetVisualDescendants().OfType<DispatcherOperationsControl>().Single().OperationCount);
        Snapshot(window, "dispatcher-queue.png");
        view.Model.SelectObject(5, false); view.ShowXamlOrigins(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.GetVisualDescendants().OfType<XamlOriginsControl>().Single().OriginCount);
        view.ShowSources(); Dispatcher.UIThread.RunJobs();
        var sources = view.GetVisualDescendants().OfType<SourceExplorer>().Single(); Assert.Equal(site, sources.SelectedSite);
        using var source = new MemoryStream(bytes); await sources.ShowSourceAsync(source);
        Assert.Contains("PDB checksum verified", sources.StatusText); Snapshot(window, "xaml-source.png");
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout);
        Assert.Equal("Investigate", view.WorkspacePresetName); Assert.Null(view.WorkspaceError); window.Close();
    }
    [AvaloniaFact] public void EmbeddedRiderThemeDoesNotChangeHostThemeOrPalette()
    {
        var app = Application.Current!; var requested = app.RequestedThemeVariant;
        app.RequestedThemeVariant = ThemeVariant.Light;
        try
        {
            WorkbenchStyles.Install(app); WorkspaceServices.Storage = new MemoryWorkspaceStorage();
            using var view = new WorkbenchView(); var sibling = new DataGrid();
            var root = new Grid { ColumnDefinitions = new("*,150") }; root.Children.Add(view); Grid.SetColumn(sibling, 1); root.Children.Add(sibling);
            var window = new Window { Width = 1750, Height = 1000, Content = root }; window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant); Assert.Equal(ThemeVariant.Light, sibling.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, view.GetVisualDescendants().OfType<SourceExplorer>().FirstOrDefault()?.ActualThemeVariant ??
                view.GetVisualDescendants().OfType<ThemeVariantScope>().First().ActualThemeVariant);
            var scope = view.GetVisualDescendants().OfType<ThemeVariantScope>().First();
            Assert.True(scope.TryFindResource("DockSurfaceWorkbenchBrush", out var surface));
            Assert.Equal("#ff2b2d30", ((Avalonia.Media.ISolidColorBrush)surface!).Color.ToString().ToLowerInvariant());
            Assert.False(app.Resources.ContainsKey("DockSurfaceWorkbenchBrush")); window.Close();
        }
        finally { app.RequestedThemeVariant = requested; }
    }
    [AvaloniaFact] public void NativeFloatingDocumentRetainsScopedRiderPalette()
    {
        var managed = Dock.Settings.DockSettings.UseManagedWindows;
        Dock.Settings.DockSettings.UseManagedWindows = false;
        try
        {
            WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
            var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
            var root = (Dock.Model.Controls.IRootDock)view.GetVisualDescendants().OfType<Dock.Avalonia.Controls.DockControl>().First().Layout!;
            var factory = (DiagnosticsVisualizer.Workbench.Docking.WorkbenchDockFactory)root.Factory!;
            factory.FloatDockable(factory.Panels["dispatcher"]); Dispatcher.UIThread.RunJobs();
            var floating = Assert.Single(root.Windows!);
            var host = Assert.IsType<Dock.Avalonia.Controls.HostWindow>(floating.Host);
            Assert.Equal(ThemeVariant.Dark, host.ActualThemeVariant);
            Assert.True(host.TryFindResource("DockDocumentTabSelectedForegroundBrush", out var foreground));
            Assert.Equal(Avalonia.Media.Colors.White, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(foreground).Color);
            Assert.Single(host.GetVisualDescendants().OfType<DispatcherOperationsControl>());
            view.SaveWorkspace(); Assert.Null(view.WorkspaceError); floating.Exit(); window.Close();
        }
        finally { Dock.Settings.DockSettings.UseManagedWindows = managed; }
    }
    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); var output = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(output);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap); bitmap.Save(Path.Combine(output, name));
    }
}
