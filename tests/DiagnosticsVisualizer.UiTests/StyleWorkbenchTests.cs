using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class StyleWorkbenchTests
{
    [AvaloniaFact] public void StylingTreeHistoryIdentityNavigationAndDockPersistenceWork()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show();
        view.ApplyWorkspacePreset(WorkspacePreset.Styling); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<StyleEvaluationsControl>().Single();
        Assert.Equal(12, control.EvaluationCount);
        var calls = control.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetName(g) == "Styling evaluation calls");
        var rows = calls.ItemsSource.Cast<StyleEvaluation>().ToArray(); Assert.Equal(2, rows.Length);
        Assert.Equal("Conditional (activator)", rows[1].Status); calls.SelectedItem = rows[0];
        var chart = control.GetVisualDescendants().OfType<StyleEvaluationChart>().Single(); chart.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(rows[1].EventId, view.Model.SelectedEventId);
        long inspected = 0; control.ObjectSelected += id => inspected = id;
        Click("Inspect activator"); Assert.Equal(rows[1].ActivatorId, inspected);
        calls.SelectedItem = rows[1]; TraceRange? requested = null; control.RangeRequested += range => requested = range;
        Click("Analyze selected call"); Assert.Equal(new TraceRange(rows[1].Completion!.Value.Timestamp, rows[1].Completion!.Value.End), requested);
        Snapshot("style-evaluations.png");
        // An attachment's source-style identity connects observations of its frames without claiming a unique owner.
        var tabs = control.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var history = control.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetName(g) == "Related style activation history");
        var activity = history.ItemsSource.Cast<StyleEvaluation>().ToArray(); Assert.Equal(4, activity.Length);
        history.SelectedItem = activity[1]; Assert.Equal(activity[1].EventId, view.Model.SelectedEventId);
        Assert.Equal(activity[1], control.CurrentEvaluation); Click("Inspect context"); Assert.Equal(101, inspected);
        Assert.False(control.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Inspect target").IsEnabled);
        Snapshot("style-activation-history.png");
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout);
        Assert.Equal("Styling", view.WorkspacePresetName); Assert.Null(view.WorkspaceError); window.Close();
        void Click(string text) => control.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == text)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        void Snapshot(string name)
        {
            Dispatcher.UIThread.RunJobs(); var directory = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(directory);
            using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap); bitmap.Save(Path.Combine(directory, name));
        }
    }
    [AvaloniaFact] public void StylingFiltersDoNotTurnConditionalResultsIntoActivity()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1400, Height = 900, Content = view }; window.Show(); view.ShowStyleEvaluations(); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<StyleEvaluationsControl>().Single();
        var mode = control.GetVisualDescendants().OfType<ComboBox>().Single(); mode.SelectedIndex = 4;
        var roots = control.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetName(g) == "Styling evaluation requests");
        Assert.Equal(4, roots.ItemsSource.Cast<StyleEvaluation>().Count()); Assert.All(roots.ItemsSource.Cast<StyleEvaluation>(), row => Assert.True(row.Status is "Active" or "Inactive"));
        var search = control.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark?.StartsWith("Find selector", StringComparison.Ordinal) == true);
        search.Text = "Conditional"; Dispatcher.UIThread.RunJobs(); Assert.Empty(roots.ItemsSource.Cast<object>());
        mode.SelectedIndex = 2; Assert.Equal(4, roots.ItemsSource.Cast<object>().Count()); window.Close();
    }
}
