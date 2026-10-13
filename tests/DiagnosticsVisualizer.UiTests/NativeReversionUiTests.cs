using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

public sealed class NativeReversionUiTests
{
    [AvaloniaFact]
    public async Task ReversionResultsRemainDistinctFromExecutionAndIncompleteHistory()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        using var view = new WorkbenchView(new WorkbenchModel(loadDemo: false));
        var window = new Window { Width = 1600, Height = 1050, Content = view }; window.Show();
        try
        {
            var store = new TraceStore();
            foreach (var block in DemoTraceFactory.CreateNative())
                store.Add(block with { Symbols = block.Symbols.Select(s =>
                {
                    if (!NativeMetadataSymbol.TryDecode(s.Name, out var metadata)) return s;
                    return new Symbol(s.Id, NativeMetadataSymbol.Encode(metadata switch
                    {
                        NativeProcessInfo p => p with { FormatVersion = 3 },
                        NativeMethodInfo m => m with { Status = m.SiteId == 2 ? -8 : 3, HResult = m.SiteId == 2 ? unchecked((int)0x80004005) : 0 },
                        _ => metadata!
                    }));
                }).ToArray() });
            using var input = new MemoryStream(); await TraceExport.WriteBinaryAsync(input, store.GetView()); input.Position = 0;
            await view.Model.ImportAsync(input, "SYNTHETIC v3 reversion / failure fixture");
            view.ApplyWorkspacePreset(WorkspacePreset.Native); Dispatcher.UIThread.RunJobs();
            var control = Assert.Single(view.GetVisualDescendants().OfType<NativeCaptureControl>());
            var index = view.Model.Analysis!.NativeCapture!;
            Assert.Equal(32, index.PairedSpans); Assert.Equal(0, index.InvalidGroups); Assert.True(index.Incomplete);
            Assert.Equal(2, index.Methods.Count(m => m.Status == "Reverted for future calls"));
            Assert.Equal("0x80004005", Assert.Single(index.Methods.Where(m => m.Status == "Revert failed")).Error);
            Assert.Contains("format v3", control.SummaryText); Assert.Contains("Incomplete coverage", control.SummaryText);
            Assert.Contains("not that old active frames ended", control.SummaryText);
            Assert.DoesNotContain("disabled probes remain", control.SummaryText);
            Assert.False(view.Model.SupportsRecordingControl);
            var folder = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(folder);
            using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); image.Save(Path.Combine(folder, "native-reversion.png"));
        }
        finally { window.Close(); }
    }
}
