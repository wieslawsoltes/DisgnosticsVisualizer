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
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class RoutedWorkbenchTests
{
    [AvaloniaFact] public void RoutedSelectionNavigationAndWorkspaceRestoreUseSharedControls()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show();
        view.ApplyWorkspacePreset(WorkspacePreset.Investigate); view.ShowRoutedEvents(); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<RoutedEventsControl>().Single();
        Assert.True(control.DispatchCount > 0);
        Assert.True(control.TryFindResource("DataGridRowSelectedBackgroundBrush", out var selectedBrush));
        Assert.Equal("#ff2e4365", Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(selectedBrush).Color.ToString().ToLowerInvariant());
        var row = control.SelectedDispatch!; Assert.True(row.ChangedHandled > 0); Assert.True(row.Skipped > 0);
        var steps = control.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetName(g) == "Routed handler steps");
        var first = row.Steps.First(s => s.ChangedHandled); var second = row.Steps.Last();
        steps.SelectedItem = first; Dispatcher.UIThread.RunJobs(); Assert.Equal(first.EventId, view.Model.SelectedEventId);
        steps.SelectedItem = second; Dispatcher.UIThread.RunJobs(); Assert.Equal(second.EventId, view.Model.SelectedEventId);
        view.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(first.EventId, view.Model.SelectedEventId); Assert.Equal(first.EventId, ((RoutedStep)steps.SelectedItem!).EventId);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(second.EventId, view.Model.SelectedEventId);
        var chart = control.GetVisualDescendants().OfType<RoutedRouteChart>().Single(); chart.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.Equal(row.Steps[^2].EventId, view.Model.SelectedEventId);
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs(); using (var bitmap = window.CaptureRenderedFrame()) { Assert.NotNull(bitmap); bitmap.Save(Path.Combine(directory, "routed-events.png")); }
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout);
        Assert.Equal("Investigate", view.WorkspacePresetName); Assert.Null(view.WorkspaceError);
        window.Close();
    }

    [AvaloniaFact] public async Task NavigationIsBoundedBranchesAndResetsForNewCaptures()
    {
        await using var model = new WorkbenchModel();
        var events = model.Trace!.Events.Where(e => e.ObjectId != 0).Take(200).ToArray();
        foreach (var e in events) model.SelectEvent(e.Id, false);
        Assert.Equal(128, model.NavigationCount);
        Assert.True(model.NavigateBack()); Assert.Equal(events[^2].Id, model.SelectedEventId); Assert.True(model.CanNavigateForward);
        model.SelectEvent(events[0].Id, false); Assert.False(model.CanNavigateForward);
        var count = model.NavigationCount; model.SelectEvent(events[0].Id, false); Assert.Equal(count, model.NavigationCount);
        model.SelectObject(1, false); Assert.Equal(0, model.SelectedEventId); Assert.True(model.NavigateBack());
        using var stream = new MemoryStream();
        await TraceFile.WriteAsync(stream, new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000 }); stream.Position = 0;
        await model.ImportAsync(stream, "Empty replacement fixture");
        Assert.Equal(1, model.NavigationCount); Assert.False(model.CanNavigateBack); Assert.False(model.CanNavigateForward);
    }
}
