using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class PropertyHistoryUiTests
{
    [AvaloniaFact] public void PropertyWorkspaceShowsDiscreteNotificationsAndPreservesSelectionAndLayout()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1100, Content = view }; window.Show();
        view.ApplyWorkspacePreset(WorkspacePreset.Properties); view.ShowPropertyHistory(); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<PropertyHistoryControl>().Single(); Assert.Equal(8, control.OperationCount);
        control.ShowHistory(); Dispatcher.UIThread.RunJobs();
        var chart = control.GetVisualDescendants().OfType<PropertyValueChart>().Single(); Assert.Equal(5, chart.ObservationCount);
        var entry = view.Model.Analysis!.PropertyOperations!.Calls.Single(c => c.Kind == PropertyOperationKind.ValueEntry);
        view.Model.SelectEvent(entry.EventId, false); Dispatcher.UIThread.RunJobs(); Assert.Equal(entry.Id, control.SelectedOperation!.Id);
        var inspect = control.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Inspect target");
        inspect.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.Equal(5, view.Model.SelectedObjectId);
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Properties", view.WorkspacePresetName); Assert.Null(view.WorkspaceError);
        Assert.Single(view.GetVisualDescendants().OfType<PropertyHistoryControl>());
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(dir);
        using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); image.Save(Path.Combine(dir, "property-history.png")); window.Close();
    }
    [AvaloniaFact] public void PartialOrPrivateNotificationsCannotFabricatePlotPointsOrIntervals()
    {
        var store = new TraceStore(); store.Add(new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000,
            Symbols = [new(1, "Incomplete notification")], Events = [new(2, 1, 10, 0, 3, 4, 1, 1, Subsystem.Properties,
                TraceEventKind.Link, PropertyOperationEvent.Code(PropertyOperationKind.Notify, 1, 8))] });
        var trace = store.GetView(); var control = new PropertyHistoryControl();
        var window = new Window { Width = 1100, Height = 900, Content = WorkbenchStyles.CreateScope(control) }; window.Show();
        control.SetTrace(trace, new(trace)); control.ShowHistory(); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Incomplete", control.SelectedOperation!.Status);
        Assert.Equal(0, control.GetVisualDescendants().OfType<PropertyValueChart>().Single().ObservationCount);
        Assert.False(control.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Analyze call").IsEnabled);
        window.Close();
    }
}
