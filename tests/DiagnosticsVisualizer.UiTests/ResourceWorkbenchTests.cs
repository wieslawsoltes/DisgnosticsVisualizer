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

public sealed class ResourceWorkbenchTests
{
    [AvaloniaFact] public void ResourceTreeSelectionFilteringKeyboardAndSavedLayoutWork()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show();
        view.ApplyWorkspacePreset(WorkspacePreset.Resources); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<ResourceLookupsControl>().Single();
        Assert.True(control.LookupCount > 20); var root = control.SelectedLookup!; Assert.Equal("SystemAccentColor", root.Key);
        var calls = control.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetName(g) == "Resource lookup provider calls");
        Assert.Equal(3, calls.ItemsSource.Cast<object>().Count()); var rows = calls.ItemsSource.Cast<ResourceLookup>().ToArray();
        calls.SelectedItem = rows[1]; Assert.Equal(rows[1].EventId, view.Model.SelectedEventId);
        var chart = control.GetVisualDescendants().OfType<ResourceLookupChart>().Single(); chart.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(rows[2].EventId, view.Model.SelectedEventId);
        var valueButton = control.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Inspect returned object");
        Assert.True(valueButton.IsEnabled);
        long inspected = 0;
        control.ObjectSelected += id => inspected = id;
        valueButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(rows[2].ValueId, inspected);
        calls.SelectedItem = rows[1]; Assert.False(valueButton.IsEnabled);
        DiagnosticsVisualizer.Analysis.TraceRange? requested = null;
        control.RangeRequested += range => requested = range;
        control.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Analyze selected call")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new TraceRange(rows[1].Completion!.Value.Timestamp, rows[1].Completion!.Value.End), requested);
        var all = control.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "All calls"); all.IsChecked = true;
        var requests = control.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetName(g) == "Resource lookup requests");
        requests.SelectedItem = rows[1]; Assert.Equal(rows[1].Id, control.SelectedLookup!.Id);
        var filter = control.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark?.StartsWith("Find resource", StringComparison.Ordinal) == true);
        filter.Text = "OptionalAccent"; Dispatcher.UIThread.RunJobs(); Assert.All(requests.ItemsSource.Cast<ResourceLookup>(), r => Assert.Equal("OptionalAccent", r.Key));
        filter.Text = ""; all.IsChecked = false; requests.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(directory);
        using (var bitmap = window.CaptureRenderedFrame()) { Assert.NotNull(bitmap); bitmap.Save(Path.Combine(directory, "resource-lookups.png")); }
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout);
        Assert.Equal("Resources", view.WorkspacePresetName); Assert.Null(view.WorkspaceError); window.Close();
    }
}
