using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Agent.Avalonia;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class SemanticInspectorTests
{
    [AvaloniaFact]
    public void DependencyAndAsyncDocumentsRenderAndNavigateIncomingRelations()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show();
        view.Model.SelectObject(5, false); view.ShowDependencies(); Dispatcher.UIThread.RunJobs();
        var explorer = view.GetVisualDescendants().OfType<DependencyExplorer>().Single();
        Assert.Equal(5, explorer.SelectedObjectId); Assert.True(explorer.RelationshipCount >= 5);
        Snapshot(window, "dependencies-workbench.png");
        var originalLayout = view.ExportWorkspace(); view.ToggleFocusMode(); Assert.True(view.IsFocusMode);
        Assert.Equal(originalLayout, view.ExportWorkspace()); view.ToggleFocusMode(); Assert.False(view.IsFocusMode);
        view.Model.SelectObject(100, false); Dispatcher.UIThread.RunJobs();
        var table = explorer.GetVisualDescendants().OfType<DataGrid>().Single();
        var row = Assert.Single(((System.Collections.IEnumerable)table.ItemsSource!).Cast<DependencyRow>());
        Assert.Equal(5, row.ObjectId); Assert.Contains("Incoming", row.Direction); table.SelectedItem = row;
        explorer.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Inspect related object")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(5, view.Model.SelectedObjectId);
        view.ShowAsyncOperations(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, view.GetVisualDescendants().OfType<AsyncOperationsControl>().Single().OperationCount);
        Snapshot(window, "async-workbench.png");
        var json = view.ExportWorkspace(); view.ImportWorkspace(json); Dispatcher.UIThread.RunJobs();
        Assert.Null(view.WorkspaceError); Assert.Contains("dependencies", view.ExportWorkspace()); window.Close();
    }
    [AvaloniaFact]
    public void ResourceCandidatesDoNotMaterializeDeferredOrInvokeUserToString()
    {
        using var session = Probe.Start(); using var inspector = new AvaloniaInspector(session);
        var button = new Button { Name = "Selected" }; var parent = new StackPanel(); parent.Children.Add(button);
        var calls = 0; var deferred = new ResourceDictionary(); deferred.AddDeferred("Lazy", _ => { calls++; return new object(); });
        parent.Resources["AncestorToken"] = "private"; button.Resources.MergedDictionaries.Add(deferred);
        button.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["Accent"] = 42 };
        button.Resources["UserValue"] = new ThrowingText();
        var relations = new List<RelationSnapshot>(); var id = session.GetObjectId(button);
        var properties = inspector.InspectProperties(id, relations);
        Assert.Equal(0, calls); Assert.Contains(properties, p => p.Name == "Lazy" && p.Kind == "Resource candidate");
        Assert.Contains(properties, p => p.Name == "Accent" && p.Source.Contains("Dark"));
        Assert.Contains(properties, p => p.Name == "AncestorToken" && p.Value == "<redacted>");
        Assert.Contains(relations, r => r.Kind == "Merged dictionary"); Assert.Contains(relations, r => r.Kind == "Theme dictionary");
        Assert.Contains(relations, r => r.Kind == "Resource scope" && r.Target == session.GetObjectId(parent));
    }
    [AvaloniaFact]
    public void ResourceInventoryReportsBudgetExhaustion()
    {
        using var session = Probe.Start(); using var inspector = new AvaloniaInspector(session); var button = new Button();
        for (var i = 0; i < 600; i++) button.Resources[$"Item{i}"] = i;
        var rows = inspector.InspectProperties(session.GetObjectId(button));
        Assert.Equal(512, rows.Count(p => p.Kind == "Resource candidate"));
        Assert.Contains(rows, p => p.Kind == "Inspection limit");
    }
    private sealed class ThrowingText { public override string ToString() => throw new InvalidOperationException("Must not be called"); }
    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); var output = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(output);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap); bitmap.Save(Path.Combine(output, name));
    }
}
