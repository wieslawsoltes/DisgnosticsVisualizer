using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class NativeWorkspaceTests
{
    [AvaloniaFact] public async Task NativeImportNavigationAndDockingRoundtrip()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        using var view = new WorkbenchView(new WorkbenchModel(loadDemo: false));
        var window = new Window { Width = 1680, Height = 1000, Content = view }; window.Show();
        try
        {
        var fixture = Environment.GetEnvironmentVariable("AVTRACE_NATIVE_UI_RECORDING");
        await using var stream = fixture is null ? await Synthetic() : File.OpenRead(fixture);
        await view.Model.ImportAsync(stream, fixture is null ? "SYNTHETIC native test fixture" : Path.GetFileName(fixture));
        view.ApplyWorkspacePreset(WorkspacePreset.Native); Dispatcher.UIThread.RunJobs();
        var control = Assert.Single(view.GetVisualDescendants().OfType<NativeCaptureControl>());
        Assert.True(control.MethodCount > 0); Assert.Contains("OS threads", control.SummaryText);
        Assert.NotNull(view.Model.Analysis!.NativeCapture); Assert.Equal(0, view.Model.Analysis.NativeCapture.InvalidGroups);
        var method = view.Model.Analysis.NativeCapture.Methods.First(m => m.EventId > 0);
        var grid = control.GetVisualDescendants().OfType<DataGrid>().First(); grid.SelectedItem = method; Dispatcher.UIThread.RunJobs();
        Assert.Equal(method.EventId, view.Model.SelectedEventId);
        var detailRows = view.GetVisualDescendants().OfType<EventDetailsControl>().Single()
            .GetVisualDescendants().OfType<DataGrid>().Single().ItemsSource.Cast<DetailRow>().ToArray();
        Assert.Contains(detailRows, row => row.Field == "Native thread slot");
        Assert.Contains(detailRows, row => row.Field == "OS thread ID" && row.Value != "Not retained");
        Assert.DoesNotContain(detailRows, row => row.Field == "Managed thread");
        if (fixture is null)
        {
            grid.SelectedItem = view.Model.Analysis.NativeCapture.Methods.Single(m => m.SiteId == 6);
            var reveal = control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Original method identity"));
            Assert.True(reveal.IsEnabled); reveal.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Synthetic.NotExecuted", view.GetVisualDescendants().OfType<SourceExplorer>().Single().SelectedSite!.Signature);
            Assert.Equal(method.EventId, view.Model.SelectedEventId); // Definition navigation did not fabricate an event.
            view.ShowNativeCapture(); Dispatcher.UIThread.RunJobs();
        }
        var layout = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(layout); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Native", view.WorkspacePresetName); Assert.Null(view.WorkspaceError);
        Assert.Single(view.GetVisualDescendants().OfType<NativeCaptureControl>());
        var folder = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(folder);
        using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); image.Save(Path.Combine(folder, "native-capture-current.png"));
        }
        finally { window.Close(); }
    }
    private static async Task<Stream> Synthetic()
    {
        var stream = new MemoryStream(); var session = Guid.NewGuid(); const long cookie = (1L << 48) + 1;
        var site = new SourceSite("Synthetic", Guid.NewGuid(), 0x06000001, "Synthetic.NativeMethod", "", 0, 0, 0, 0, "", "", "", false);
        await TraceFile.WriteAsync(stream, new TraceBlock { SessionId = session, Frequency = 1000, Sequence = 1,
            Symbols = [new(1, SourceSiteSymbol.Encode(site)), new(2, NativeMetadataSymbol.Encode(new NativeProcessInfo(100, 8))),
                new(3, NativeMetadataSymbol.Encode(new NativeThreadInfo(1, 500))), new(4, NativeMetadataSymbol.Encode(new NativeMethodInfo(1, 2, 0, 1))),
                new(6, SourceSiteSymbol.Encode(site with { Token = 0x06000002, Signature = "Synthetic.NotExecuted" })),
                new(7, NativeMetadataSymbol.Encode(new NativeMethodInfo(6, 1, 0, 0)))] });
        await TraceFile.WriteAsync(stream, new TraceBlock { SessionId = session, Frequency = 1000, Sequence = 2,
            Events = [new(cookie * 2, 0, 10, 0, 0, 0, 1, 1, Subsystem.Application, TraceEventKind.Link, NativeMethodEvent.BeginCode, cookie),
                new(cookie * 2 + 1, 0, 10, 4, 0, 0, 1, 1, Subsystem.Application, TraceEventKind.Span, NativeMethodEvent.ReturnedCode, cookie)] });
        await TraceFile.WriteAsync(stream, new TraceBlock { SessionId = session, Frequency = 1000, Sequence = 3,
            Symbols = [new(5, NativeMetadataSymbol.Encode(new NativeCaptureEnd(true, false, 0, 20, 0, 0, 0, 1, 1, 1, 0, 0, 2, 300)))] });
        stream.Position = 0; return stream;
    }
}
