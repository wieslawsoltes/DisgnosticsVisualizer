using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;

namespace DiagnosticsVisualizer.Controls;

public sealed class DispatcherOperationsControl : UserControl
{
    private readonly DispatcherQueueChart _chart = new();
    private readonly DataGrid _grid = DiagnosticGrid.Create(("Callback / queue site", "Name", -1), ("Outcome", "Status", 165),
        ("Queue ms", "QueueMs", 110), ("Runnable ms", "RunnableQueueMs", 125), ("Inactive ms", "InactiveQueueMs", 120),
        ("Callback ms", "ExecutionMs", 125), ("Posted priority", "PostedPriority", 140), ("Run priority", "ExecutionPriority", 130),
        ("Post thread", "PostingThread", 120), ("Run thread", "ExecutionThread", 120));
    private readonly TextBox _search = new() { Watermark = "Find callback or queue site…", MaxLength = 512, MinWidth = 180, FontSize = 11 };
    private readonly ComboBox _outcome = new() { ItemsSource = new[] { "All outcomes", "Returned", "Callback threw", "Aborted before execution", "Incomplete / invalid" }, SelectedIndex = 0, Width = 180, FontSize = 11 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(12, 6), FontSize = 11 };
    private DispatcherOperation[] _rows = [];
    private TraceView? _trace;
    private bool _updating;
    public event Action<long>? EventSelected;
    public event Action<TraceRange>? RangeRequested;
    public int OperationCount => _rows.Length;
    public DispatcherOperationsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*") };
        root.Children.Add(new TextBlock { Text = "Queue admission → callback → return / throw / abort. Timer inactivity is not runnable delay; callbacks can await further work after returning.",
            Margin = new(12, 8), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(_summary, 1); root.Children.Add(_summary); Grid.SetRow(_chart, 2); root.Children.Add(_chart);
        var tools = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Margin = new(10, 6) };
        tools.Children.Add(_search); Grid.SetColumn(_outcome, 1); tools.Children.Add(_outcome);
        var range = new Button { Content = "Analyze interval", FontSize = 11 };
        range.Click += (_, _) =>
        {
            if (_grid.SelectedItem is DispatcherOperation { Queued: { } q, Finished: { } f, QueueMs: not null } && f.Timestamp > q.Timestamp)
                RangeRequested?.Invoke(new(q.Timestamp, f.Timestamp));
        };
        Grid.SetColumn(range, 2); tools.Children.Add(range); Grid.SetRow(tools, 3); root.Children.Add(tools);
        Grid.SetRow(_grid, 4); root.Children.Add(_grid); Content = root;
        AutomationProperties.SetName(_grid, "Dispatcher operations"); AutomationProperties.SetName(_search, "Find dispatcher operation");
        _search.TextChanged += (_, _) => Refresh(); _outcome.SelectionChanged += (_, _) => Refresh();
        _grid.SelectionChanged += (_, _) =>
        { if (!_updating && _grid.SelectedItem is DispatcherOperation row) { _chart.Select(row.Id); EventSelected?.Invoke(row.EventId); } };
        _chart.OperationSelected += row => { _grid.SelectedItem = row; _grid.ScrollIntoView(row, null); };
    }
    public void SetTrace(TraceView trace, DispatcherOperation[] operations)
    {
        if (ReferenceEquals(trace, _trace)) return;
        _trace = trace; _rows = operations;
        var waits = _rows.Where(r => r.RunnableQueueMs is not null).Select(r => r.RunnableQueueMs!.Value).Order().ToArray();
        _summary.Text = _rows.Length == 0 ? "No dispatcher lifecycle markers. Enable AVTRACE_DISPATCHER_OPERATIONS=1 on instrumented output." :
            $"{_rows.Length:N0} admissions / partial operations · {_rows.Count(r => r.Status == "Callback threw"):N0} callback faults · {_rows.Count(r => r.Status == "Aborted before execution"):N0} aborted" +
            (waits.Length == 0 ? " · runnable delay unknown" : $" · observed runnable p95 {waits[(int)Math.Ceiling(waits.Length * .95) - 1]:N3} ms") +
            " · chart shows six longest retained queue residences";
        Refresh();
    }
    private void Refresh()
    {
        var text = _search.Text ?? "";
        var rows = _rows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) &&
            (_outcome.SelectedIndex <= 0 || _outcome.SelectedIndex == 4 && (r.Status.StartsWith("Incomplete", StringComparison.Ordinal) || r.Status.StartsWith("Conflicting", StringComparison.Ordinal)) ||
             _outcome.SelectedIndex is > 0 and < 4 && r.Status == (string?)_outcome.SelectedItem)).ToArray();
        _updating = true;
        try { var id = (_grid.SelectedItem as DispatcherOperation)?.Id; _grid.ItemsSource = rows; _grid.SelectedItem = rows.FirstOrDefault(r => r.Id == id); }
        finally { _updating = false; }
        if (_trace is { } trace) _chart.SetOperations(rows, trace);
    }
}
