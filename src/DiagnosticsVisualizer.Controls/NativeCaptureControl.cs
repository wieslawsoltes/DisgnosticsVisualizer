using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;

namespace DiagnosticsVisualizer.Controls;

public sealed class NativeCaptureControl : UserControl
{
    private readonly TextBlock _summary = new() { Margin = new(12), TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 22 };
    private readonly TextBox _filter = new() { Watermark = "Find native-profiled method…", MaxLength = 512, Margin = new(10, 4), MinWidth = 180, FontSize = 11 };
    private readonly DataGrid _methods = DiagnosticGrid.Create(("Selected method", "Method", -1), ("Status", "Status", 160), ("ReJITs", "Compilations", 75),
        ("Paired spans", "Completions", 105), ("Unwound", "Unwound", 90), ("Open", "UnmatchedEntries", 65), ("Total ms", "TotalMs", 100), ("HRESULT", "Error", 105), ("Original MVID / token", "MethodIdentity", 300));
    private readonly DataGrid _threads = DiagnosticGrid.Create(("Native slot", "Slot", 110), ("Operating-system thread ID (not managed ID)", "OsThreadId", -1));
    private readonly Button _source = new() { Content = "Original method identity", FontSize = 11, Margin = new(4), Padding = new(8, 4), IsEnabled = false };
    private NativeCaptureIndex? _index;
    private bool _updating;
    public event Action<long>? EventSelected;
    public event Action<long>? SourceRequested;
    public int MethodCount => _index?.Methods.Length ?? 0;
    public string SummaryText => _summary.Text ?? "";
    public NativeCaptureControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,5,130") };
        root.Children.Add(new TextBlock { Text = "NATIVE CLR / ReJIT · selected synchronous method lifetimes. Native thread slots are not managed thread IDs. No Avalonia object tree is implied.",
            TextWrapping = TextWrapping.Wrap, Margin = new(12, 8), FontSize = 10, Foreground = ChartPalette.Muted });
        Grid.SetRow(_summary, 1); root.Children.Add(_summary);
        var actions = new Grid { ColumnDefinitions = new("*,Auto") }; actions.Children.Add(_filter);
        var source = _source;
        source.Click += (_, _) => { if (_methods.SelectedItem is NativeMethodRow { EventId: > 0 } row) SourceRequested?.Invoke(row.EventId); };
        Grid.SetColumn(source, 1); actions.Children.Add(source); Grid.SetRow(actions, 2); root.Children.Add(actions);
        Grid.SetRow(_methods, 3); root.Children.Add(_methods);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 4); root.Children.Add(splitter); Grid.SetRow(_threads, 5); root.Children.Add(_threads); Content = root;
        AutomationProperties.SetName(_methods, "Native method coverage"); AutomationProperties.SetName(_threads, "Native operating-system threads");
        _filter.TextChanged += (_, _) => Refresh();
        _methods.SelectionChanged += (_, _) => { if (!_updating && (_source.IsEnabled = _methods.SelectedItem is NativeMethodRow { EventId: > 0 }) && _methods.SelectedItem is NativeMethodRow row) EventSelected?.Invoke(row.EventId); };
    }
    public void SetTrace(TraceView trace, NativeCaptureIndex? index)
    {
        _index = index;
        _threads.ItemsSource = index?.Threads;
        _summary.Text = index?.Process is not { } process ? "No native recorder provenance. Open a .dvntrace file created by the explicit NativeCapture attach command; opening a recording never attaches or executes code." :
            $"Process {process.ProcessId} · format v{process.FormatVersion}{(process.FormatVersion == 1 ? " (legacy header without checksum)" : "")} · {process.PointerBytes * 8}-bit · {index.Methods.Length:N0} selected definitions · {index.Threads.Length:N0} OS threads\n" +
            $"{index.PairedSpans:N0} paired spans · {index.UnmatchedEntries:N0} unmatched entries · {index.UnmatchedCompletions:N0} unmatched completions · {index.InvalidGroups:N0} invalid groups\n" +
            (index.End is { HasFooter: true } end ? $"Terminal status: {StopReason(end.Reason)} · {end.DroppedRecords:N0} drops · {end.RecorderFaults:N0} recorder faults · {end.IoFaults:N0} I/O faults · {end.RejitErrors:N0} ReJIT errors\n" : "Terminal integrity is UNKNOWN: no complete footer was retained.\n") +
            (index.Incomplete ? "Incomplete coverage; missing observations cannot establish that a call did not occur. " : "Complete terminal metadata for this retained capture. ") +
            "IL supplied is not execution proof. Stopping capture does not unload/revert the profiler; disabled probes remain until process exit.";
        Refresh();
    }
    private void Refresh()
    {
        var old = (_methods.SelectedItem as NativeMethodRow)?.SiteId;
        var rows = _index?.Methods.Where(m => m.Method.Contains(_filter.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        _updating = true;
        try { _methods.ItemsSource = rows; _methods.SelectedItem = rows.FirstOrDefault(r => r.SiteId == old); }
        finally { _updating = false; _source.IsEnabled = _methods.SelectedItem is NativeMethodRow { EventId: > 0 }; }
    }
    private static string StopReason(int reason) => reason switch { 0 => "duration reached", 1 => "runtime shutdown", 2 => "worker failure", 3 => "file budget reached", _ => "unavailable" };
}
