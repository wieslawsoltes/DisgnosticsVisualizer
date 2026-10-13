using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class GpuWorkspaceTests
{
    [AvaloniaFact] public async Task GpuQuerySelectionCpuRangeAndLayoutAreIndependentOfDeviceClock()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        using var view = new WorkbenchView(new WorkbenchModel(loadDemo: false));
        var window = new Window { Width = 1680, Height = 1000, Content = view }; window.Show();
        try
        {
            var path = Environment.GetEnvironmentVariable("AVTRACE_GPU_UI_RECORDING");
            if (path is not null) { await using var input = File.OpenRead(path); await view.Model.ImportAsync(input, "Recorded EGL/Skia software-renderer validation"); }
            else view.Model.LoadGpuDemo();
            view.ApplyWorkspacePreset(WorkspacePreset.Gpu); Dispatcher.UIThread.RunJobs();
            var control = Assert.Single(view.GetVisualDescendants().OfType<GpuTimingsControl>());
            Assert.True(control.AvailableCount > 0);
            if (path is null) { Assert.Equal(12, control.QueryCount); Assert.Equal(10, control.AvailableCount); Assert.True(view.Model.IsDemo); }
            var grid = control.GetVisualDescendants().OfType<DataGrid>().Single();
            var query = view.Model.Analysis!.GpuTimings!.Rows.First(r => r.IsAvailable);
            grid.SelectedItem = query; Dispatcher.UIThread.RunJobs();
            Assert.Equal(query.EventId, view.Model.SelectedEventId);
            TraceRange? range = null; control.RangeRequested += selected => range = selected;
            var button = control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Analyze CPU submission"));
            Assert.True(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new TraceRange(query.Begin!.Value.Timestamp, query.Submitted!.Value.Timestamp), range);
            var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Inspect); view.ImportWorkspace(layout); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Gpu", view.WorkspacePresetName); Assert.Null(view.WorkspaceError);
            Assert.DoesNotContain("Synthetic GPU", layout); // No device/capture data in workspace settings.
            var folder = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(folder);
            using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); image.Save(Path.Combine(folder, "gpu-timings.png"));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void UnavailableQueriesNeverEnableFabricatedCpuRange()
    {
        using var model = new AsyncModelLease(new WorkbenchModel(loadDemo: false));
        model.Value.LoadGpuDemo();
        var control = new GpuTimingsControl(); control.SetTrace(model.Value.Trace!, model.Value.Analysis!.GpuTimings!);
        var window = new Window { Width = 900, Height = 650, Content = control }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var grid = control.GetVisualDescendants().OfType<DataGrid>().Single();
            grid.SelectedItem = model.Value.Analysis!.GpuTimings!.Rows.Single(r => r.Status == "Unsupported");
            var range = control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Analyze CPU submission"));
            Assert.False(range.IsEnabled);
        }
        finally { window.Close(); }
    }
    private sealed class AsyncModelLease(WorkbenchModel model) : IDisposable
    { public WorkbenchModel Value => model; public void Dispose() => model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
