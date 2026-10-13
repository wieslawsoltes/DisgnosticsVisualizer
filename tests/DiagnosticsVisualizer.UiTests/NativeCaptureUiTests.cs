using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class NativeCaptureUiTests
{
    [AvaloniaFact] public void NativeCoverageNavigationAndWorkspacePersistence()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1050, Content = view }; window.Show();
        view.Model.LoadNativeDemo(); view.ApplyWorkspacePreset(WorkspacePreset.Native); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<NativeCaptureControl>().Single();
        Assert.Equal(3, control.MethodCount); Assert.Equal(32, view.Model.Analysis!.NativeCapture!.PairedSpans);
        Assert.Contains("DEMO", view.Model.Source); Assert.False(view.Model.SupportsRecordingControl);
        Assert.Contains("disabled probes remain", control.SummaryText);
        var table = control.GetVisualDescendants().OfType<DataGrid>().First(); table.SelectedIndex = 0;
        var button = control.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Original method identity");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.GetVisualDescendants().OfType<SourceExplorer>().Single().SelectedSite);
        view.ShowNativeCapture(); Dispatcher.UIThread.RunJobs();
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Native", view.WorkspacePresetName); Assert.Null(view.WorkspaceError);
        Assert.Single(view.GetVisualDescendants().OfType<NativeCaptureControl>());
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(directory);
        using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); image.Save(Path.Combine(directory, "native-capture.png")); window.Close();
    }
}
