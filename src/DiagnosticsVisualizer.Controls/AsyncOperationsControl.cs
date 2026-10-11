using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;

namespace DiagnosticsVisualizer.Controls;

public sealed class AsyncOperationsControl : UserControl
{
    private readonly AsyncLifetimeChart _chart = new();
    private readonly DataGrid _grid = DiagnosticGrid.Create(("Operation", "Name", -1), ("Outcome", "Status", 168),
        ("Lifetime ms", "DurationMs", 100), ("Start thread", "StartThread", 82), ("End thread", "EndThread", 82));
    private readonly TextBlock _summary = new() { FontSize = 11, Margin = new(12, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Watermark = "Find async operation…", Width = 260, FontSize = 11 };
    private readonly ComboBox _state = new() { Width = 170, FontSize = 11, ItemsSource = new[] { "All outcomes", "Succeeded", "Faulted", "Canceled", "Incomplete / invalid" }, SelectedIndex = 0 };
    private AsyncOperation[] _operations = [];
    private TraceView? _trace;
    private bool _updating;
    public event Action<long>? EventSelected;
    public event Action<TraceRange>? RangeRequested;
    public int OperationCount => _operations.Length;
    public AsyncOperationsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*") };
        var note = new TextBlock { Text = "Recorded state-machine entry → builder completion. Includes suspension, not CPU time. Missing markers are unknown, not proof an operation is still running.",
            FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(12, 8) };
        root.Children.Add(note); Grid.SetRow(_summary, 1); root.Children.Add(_summary);
        Grid.SetRow(_chart, 2); root.Children.Add(_chart);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(10, 5) };
        tools.Children.Add(_search); tools.Children.Add(_state);
        var range = new Button { Content = "Analyze lifetime range", FontSize = 11 };
        range.Click += (_, _) => { if (_grid.SelectedItem is AsyncOperation { IsComplete: true } op && op.Completion!.Value.Timestamp > op.Begin!.Value.Timestamp)
            RangeRequested?.Invoke(new(op.Begin.Value.Timestamp, op.Completion.Value.Timestamp)); };
        tools.Children.Add(range); Grid.SetRow(tools, 3); root.Children.Add(tools);
        Grid.SetRow(_grid, 4); root.Children.Add(_grid); Content = root;
        AutomationProperties.SetName(_search, "Find async operation"); AutomationProperties.SetName(_grid, "Async operation lifetimes");
        _search.TextChanged += (_, _) => Refresh(); _state.SelectionChanged += (_, _) => Refresh();
        _grid.SelectionChanged += (_, _) => { if (!_updating && _grid.SelectedItem is AsyncOperation op) EventSelected?.Invoke(op.EventId); };
        _chart.OperationSelected += op => { _grid.SelectedItem = op; _grid.ScrollIntoView(op, null); };
    }
    public void SetOperations(AsyncOperation[] operations, TraceView trace)
    {
        _operations = operations; _trace = trace;
        _summary.Text = operations.Length == 0 ? "No lifecycle markers. Enable deep weaving and AVTRACE_ASYNC_OPERATIONS=1 on the target." :
            $"{operations.Length:N0} operations · {operations.Count(o => o.IsComplete):N0} complete · {operations.Count(o => !o.IsComplete):N0} incomplete/invalid · {operations.Count(o => o.Status == "Faulted"):N0} faulted · {operations.Count(o => o.Status == "Canceled"):N0} canceled";
        Refresh();
    }
    private void Refresh()
    {
        var text = _search.Text ?? ""; var status = _state.SelectedIndex;
        var rows = _operations.Where(o => o.Name.Contains(text, StringComparison.OrdinalIgnoreCase) &&
            (status <= 0 || status == 4 && !o.IsComplete || status is > 0 and < 4 && o.Status == (string)_state.SelectedItem!)).ToArray();
        _updating = true;
        try { var selection = (_grid.SelectedItem as AsyncOperation)?.Id; _grid.ItemsSource = rows; _grid.SelectedItem = rows.FirstOrDefault(o => o.Id == selection); }
        finally { _updating = false; }
        if (_trace is not null) _chart.SetOperations(rows, _trace);
    }
}
