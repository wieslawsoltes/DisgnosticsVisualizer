using DiagnosticsVisualizer.Analysis;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchModel
{
    public void LoadGpuDemo()
    {
        if (Connected) { ReportError("Disconnect before replacing a live session with an example."); return; }
        ResetReview(); Interlocked.Increment(ref _generation); _store = new(500_000);
        foreach (var block in DemoTraceFactory.CreateGpu()) _store.Add(block);
        IsDemo = true; Source = "DEMO · synthetic GPU timestamps"; Status = "Offline example · no graphics device is monitored"; Error = "";
        var trace = _store.GetView(); Apply(trace, TraceAnalyzer.Analyze(trace)); SelectEvent(trace.Events[0].Id, false);
    }
}
