using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed class GpuTimingsControl : UserControl
{
    private readonly GpuTimingChart _chart = new();
    private readonly DataGrid _grid = DiagnosticGrid.Create(("Operation", "Name", -1), ("Outcome", "Status", 140), ("Context", "ContextId", 85),
        ("GPU ms", "GpuMs", 105), ("CPU submit ms", "SubmissionMs", 125), ("Readback delay ms", "ReadbackMs", 150), ("Renderer", "Renderer", 210));
    private readonly TextBox _search = new() { Watermark = "Find GPU operation or renderer…", MaxLength = 512, MinWidth = 170, FontSize = 11 };
    private readonly ComboBox _outcome = new() { ItemsSource = new[] { "All outcomes", "Available", "Other / incomplete" }, SelectedIndex = 0, MinWidth = 155, FontSize = 11 };
    private readonly Button _range = new() { Content = "Analyze CPU submission", FontSize = 11, IsEnabled = false };
    private readonly TextBlock _summary = new() { Margin = new(12, 5), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Margin = new(12, 4), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap };
    private GpuTimingIndex? _index;
    private Dictionary<long, GpuTimingRow> _visible = [];
    private bool _updating;
    public int QueryCount => _index?.Rows.Length ?? 0;
    public int AvailableCount => _index?.Rows.Count(r => r.IsAvailable) ?? 0;
    public long SelectedId => (_grid.SelectedItem as GpuTimingRow)?.Id ?? 0;
    public event Action<long>? EventSelected;
    public event Action<TraceRange>? RangeRequested;
    public GpuTimingsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,225,5,*,Auto") };
        root.Children.Add(new TextBlock { Text = "GPU TIMESTAMPS · uncalibrated device nanoseconds. CPU submission and readback are separate clocks/intervals; these are not utilization, presentation latency or hardware counters.",
            TextWrapping = TextWrapping.Wrap, Margin = new(12, 8), FontSize = 10, Foreground = ChartPalette.Muted });
        Grid.SetRow(_summary, 1); root.Children.Add(_summary);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(10, 5) };
        foreach (var c in new Control[] { _search, _outcome, _range }) { c.Margin = new(3); actions.Children.Add(c); }
        Grid.SetRow(actions, 2); root.Children.Add(actions); Grid.SetRow(_chart, 3); root.Children.Add(_chart);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 4); root.Children.Add(splitter); Grid.SetRow(_grid, 5); root.Children.Add(_grid);
        Grid.SetRow(_detail, 6); root.Children.Add(_detail); Content = root;
        AutomationProperties.SetName(_grid, "GPU timestamp queries"); AutomationProperties.SetName(_search, "Find GPU timestamp query");
        _search.TextChanged += (_, _) => Refresh(); _outcome.SelectionChanged += (_, _) => Refresh();
        _chart.QuerySelected += row => { _grid.SelectedItem = row; _grid.ScrollIntoView(row, null); };
        _grid.SelectionChanged += (_, _) =>
        {
            UpdateSelection();
            if (!_updating && _grid.SelectedItem is GpuTimingRow row) EventSelected?.Invoke(row.EventId);
        };
        _range.Click += (_, _) =>
        { if (_grid.SelectedItem is GpuTimingRow { Begin: { } b, Submitted: { } s, Status: not "Invalid markers" } && s.Timestamp > b.Timestamp) RangeRequested?.Invoke(new(b.Timestamp, s.Timestamp)); };
    }
    public void SetTrace(TraceView trace, GpuTimingIndex index)
    {
        if (ReferenceEquals(index, _index)) return;
        _index = index;
        _summary.Text = QueryCount == 0 ? "No GPU queries. Enable AVTRACE_GPU_TIMINGS=1 on supported, instrumented desktop OpenGL output." :
            $"{QueryCount:N0} queries · {AvailableCount:N0} available · {index.Rows.Count(r => r.Status == "Incomplete"):N0} incomplete · {index.Rows.Count(r => r.Status == "Invalid markers"):N0} invalid · {index.Rows.Select(r => r.ContextId).Distinct().Count():N0} observed contexts" +
            (trace.DroppedEvents + trace.MissingBlocks + trace.EvictedEvents + trace.MetadataDrops > 0 ? " · recording has missing evidence" : "");
        Refresh();
    }
    public void SelectEvent(TraceEvent? selected)
    {
        if (selected is not { } e || !GpuTimingEvent.IsMarker(e)) return;
        var id = e.Data0 == GpuTimingEvent.Begin ? e.Id : e.ParentId;
        _updating = true;
        try { _grid.SelectedItem = _visible.GetValueOrDefault(id); }
        finally { _updating = false; }
        UpdateSelection();
    }
    private void Refresh()
    {
        var old = SelectedId; var text = _search.Text ?? "";
        var rows = (_index?.Rows ?? []).Where(r => (r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Renderer.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
            (_outcome.SelectedIndex <= 0 || _outcome.SelectedIndex == 1 && r.IsAvailable || _outcome.SelectedIndex == 2 && !r.IsAvailable)).ToArray();
        _visible = rows.ToDictionary(r => r.Id);
        _updating = true;
        try { _grid.ItemsSource = rows; _grid.SelectedItem = rows.FirstOrDefault(r => r.Id == old); }
        finally { _updating = false; }
        _chart.SetRows(rows); UpdateSelection();
    }
    private void UpdateSelection()
    {
        var row = _grid.SelectedItem as GpuTimingRow; _chart.Select(row?.Id ?? 0);
        _range.IsEnabled = row is { Begin: { } b, Submitted: { } s, Status: not "Invalid markers" } && s.Timestamp > b.Timestamp;
        _detail.Text = row is null ? "Select a query for original device provenance. No source values or frame pixels are captured." :
            $"{row.Site?.Api} · {row.Site?.Vendor} · {row.Renderer}\n{row.Site?.Limitation}" +
            (row.ElapsedNs is { } elapsed ? $"\nExact device interval: {elapsed} ns. Absolute device timestamps have no CPU calibration." : "\nDevice interval is not available; no duration is inferred.");
    }
}
