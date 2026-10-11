using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Docking;
using DiagnosticsVisualizer.Workbench.Persistence;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Settings;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DiagnosticsVisualizer.UiTests.TestApplication))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DiagnosticsVisualizer.UiTests;

public sealed class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize() { DockSettings.UseManagedWindows = true; WorkbenchStyles.Install(this); }
}
public sealed class WorkspaceTests
{
    [AvaloniaFact]
    public async Task SourcesPanelVerifiesLocalFileAndRestoresDocking()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Contains("sources", view.ExportWorkspace());
        view.ShowSources(); Dispatcher.UIThread.RunJobs();
        var source = view.GetVisualDescendants().OfType<DiagnosticsVisualizer.Controls.SourceExplorer>().Single();
        var text = System.Text.Encoding.UTF8.GetBytes("first\nsecond\nthird");
        var site = new DiagnosticsVisualizer.Core.SourceSite("App", Guid.NewGuid(), 0x06000001, "View.Measure", "View.cs", 2, 1, 2, 6,
            "SHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(text)), "", false);
        var trace = view.Model.Trace!; var selected = trace.Events.First();
        using var capture = new MemoryStream();
        await DiagnosticsVisualizer.Protocol.TraceFile.WriteAsync(capture, new DiagnosticsVisualizer.Core.TraceBlock
        { SessionId = trace.SessionId, Sequence = 1, Symbols = [new(selected.NameId, DiagnosticsVisualizer.Core.SourceSiteSymbol.Encode(site))], Events = [selected], Snapshot = trace.Snapshot });
        capture.Position = 0; await view.Model.ImportAsync(capture, "Source navigation test fixture"); view.Model.SelectEvent(selected.Id, false);
        Dispatcher.UIThread.RunJobs();
        using var stream = new MemoryStream(text); await source.ShowSourceAsync(stream);
        Assert.Contains("PDB checksum verified", source.StatusText); Assert.Contains("2  second", source.SourceText);
        using var wrong = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("wrong source"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ShowSourceAsync(wrong));
        var dock = view.GetVisualDescendants().OfType<DockControl>().First();
        var panel = WorkbenchDockFactory.Descendants(dock.Layout!).Single(p => p.Id == "sources");
        ((IDock)panel.Owner!).ActiveDockable = panel; Dispatcher.UIThread.RunJobs();
        var output = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(output);
        using (var bitmap = window.CaptureRenderedFrame()) { Assert.NotNull(bitmap); bitmap.Save(Path.Combine(output, "sources-workbench.png")); }
        window.Close();
    }
    [AvaloniaFact]
    public void RiderWorkbenchRendersAndRestoresPreset()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.True(view.Model.Profile!.Operations.Length > 15); Assert.Equal("Rider Dark", view.ThemeName);
        Assert.True(view.GetVisualDescendants().Count() > 100);
        view.ApplyWorkspacePreset(WorkspacePreset.Profile); Dispatcher.UIThread.RunJobs(); view.SaveWorkspace();
        Assert.Null(view.WorkspaceError);
        var json = view.ExportWorkspace(); Assert.Equal(WorkspacePreset.Profile, JsonSerializer.Deserialize<WorkspaceState>(json)!.Preset);
        view.ApplyWorkspacePreset(WorkspacePreset.Inspect); view.ImportWorkspace(json); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Profile", view.WorkspacePresetName);
        var root = view.GetVisualDescendants().OfType<DockControl>().First().Layout!;
        Assert.Contains(WorkbenchDockFactory.Descendants(root).OfType<IDocumentDock>(), dock => dock.ActiveDockable?.Id == "flame");
        var output = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(output);
        using (var bitmap = window.CaptureRenderedFrame()) { Assert.NotNull(bitmap); bitmap.Save(Path.Combine(output, "rider-workbench.png")); }
        window.Close();
    }
    [AvaloniaFact]
    public void InvalidWorkspaceDoesNotMutateLiveLayout()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView(); var original = view.ExportWorkspace();
        Assert.Throws<InvalidDataException>(() => view.ImportWorkspace(original.Replace("Rider Dark", "Injected")));
        Assert.Equal(original, view.ExportWorkspace());
        Assert.Throws<InvalidDataException>(() => view.ImportWorkspace("{\"Version\":1,\"Theme\":\"Rider Dark\",\"Preset\":0,\"Root\":null}"));
    }
    [AvaloniaFact]
    public async Task RangeQueryAndBookmarksAreSynchronized()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView(); var model = view.Model;
        var trace = model.Trace!; var start = trace.Events.Min(e => e.Timestamp);
        await model.SetRangeAsync(new(start, start + trace.Frequency / 10));
        Assert.True(model.Profile!.Events < model.FullProfile!.Events);
        var rows = model.FilterEvents("subsystem:Layout duration>0ms");
        Assert.All(rows, row => { Assert.Equal(DiagnosticsVisualizer.Core.Subsystem.Layout, row.Subsystem); Assert.True(model.Range!.Value.Intersects(row.Event)); });
        model.SelectEvent(trace.Events.First().Id, false); model.AddBookmark(); model.AddBookmark(); Assert.Single(model.Bookmarks);
        Assert.Empty(model.FilterEvents("duration>bad")); Assert.NotEmpty(model.QueryError);
        model.LoadDemo(); Assert.Null(model.Range); Assert.Empty(model.Bookmarks);
    }
    [AvaloniaFact]
    public void LayoutRoundTripPreservesPinnedNodes()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var state = JsonSerializer.Deserialize<WorkspaceState>(view.ExportWorkspace())!;
        var known = new HashSet<string>(StringComparer.Ordinal);
        void Collect(DockNodeState node) { foreach (var id in node.Hidden) known.Add(id); if (node.Kind == "Panel") known.Add(node.Id); foreach (var child in node.Children) Collect(child); }
        Collect(state.Root);
        var properties = new DockNodeState { Id = "properties" };
        DockNodeState Remove(DockNodeState node) => node with
        {
            Active = node.Active == "properties" ? "details" : node.Active,
            Children = node.Children.Where(c => c.Id != "properties").Select(Remove).ToArray()
        };
        state = state with { Root = Remove(state.Root) with { Right = ["properties"] } };
        var json = JsonSerializer.Serialize(state);
        Assert.Equal("properties", Assert.Single(WorkspaceLayout.Deserialize(json, known).Root.Right));
        view.ImportWorkspace(json); Assert.Null(view.WorkspaceError);
        Assert.Equal("properties", Assert.Single(JsonSerializer.Deserialize<WorkspaceState>(view.ExportWorkspace())!.Root.Right));
    }
    [AvaloniaFact]
    public void FloatingWindowAndPinningSurviveRealDockOperations()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        var dockControl = view.GetVisualDescendants().OfType<DockControl>().First();
        var root = (IRootDock)dockControl.Layout!;
        var factory = (WorkbenchDockFactory)root.Factory!;
        factory.PinDockable(factory.Panels["properties"]); Dispatcher.UIThread.RunJobs();
        Assert.True(factory.IsDockablePinned(factory.Panels["properties"]));
        factory.FloatDockable(factory.Panels["statistics"]); Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(root.Windows!);
        view.SaveWorkspace(); Assert.Null(view.WorkspaceError);
        var json = view.ExportWorkspace();
        var state = JsonSerializer.Deserialize<WorkspaceState>(json)!;
        Assert.NotEmpty(state.Root.Windows);
        view.ImportWorkspace(json); Dispatcher.UIThread.RunJobs();
        var restored = (IRootDock)dockControl.Layout!;
        Assert.NotEmpty(restored.Windows!);
        Assert.True(factory.IsDockablePinned(factory.Panels["properties"]));
        foreach (var floating in restored.Windows!.ToArray()) floating.Exit();
        window.Close();
    }
    [AvaloniaFact]
    public void NullChildrenDuplicatePanelsAndOversizedLayoutsAreRejected()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var state = JsonSerializer.Deserialize<WorkspaceState>(view.ExportWorkspace())!;
        var corrupt = state with { Root = state.Root with { Children = [null!] } };
        Assert.Throws<InvalidDataException>(() => view.ImportWorkspace(JsonSerializer.Serialize(corrupt)));
        corrupt = state with { Root = state.Root with { Hidden = ["visual"] } };
        Assert.Throws<InvalidDataException>(() => view.ImportWorkspace(JsonSerializer.Serialize(corrupt)));
        Assert.Throws<InvalidDataException>(() => view.ImportWorkspace(new string(' ', WorkspaceLayout.MaxBytes + 1)));
    }
}
