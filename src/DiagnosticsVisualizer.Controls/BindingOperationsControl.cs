using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

/// <summary>Recorded binding call trees and chronological expression history, without executing target code.</summary>
public sealed class BindingOperationsControl : UserControl
{
    private const int RowLimit = 8192;
    private readonly TextBox _search = new() { Watermark = "Find operation or source site…", MinWidth = 180, MaxLength = 512, FontSize = 11 };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "All invocations", "Publication / delivery", "Source / converter", "Dynamic resources", "Errors / incomplete" }, SelectedIndex = 0, Width = 185, FontSize = 11 };
    private readonly CheckBox _history = new() { Content = "Expression history", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { Margin = new(10, 6), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { Margin = new(10, 5), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _requests = DiagnosticGrid.Create(("Operation", "Kind", 155), ("Expression", "ExpressionId", 100), ("Outcome", "Status", 130), ("Duration ms", "DurationMs", 100), ("Source site", "Name", -1));
    private readonly DataGrid _calls = DiagnosticGrid.Create(("Operation", "Kind", 160), ("Outcome", "Status", 130), ("Input", "InputKind", 100), ("Output", "OutputKind", 100), ("Running before / after", "Running", 155), ("Error", "Error", 130), ("Sink flags", "Changes", 220), ("Index / priority", "Detail", 120), ("Duration ms", "DurationMs", 100));
    private readonly BindingOperationChart _chart = new();
    private readonly Button _target = new() { Content = "Recorded target" }, _expression = new() { Content = "Expression" }, _participant = new() { Content = "Sink / converter / key" }, _property = new() { Content = "Property" }, _source = new() { Content = "Source" }, _range = new() { Content = "Analyze call" };
    private BindingOperationIndex? _index;
    private TraceView? _trace;
    private BindingOperation? _root, _selected;
    private bool _updating;
    public event Action<long>? EventSelected;
    public event Action<long>? ObjectSelected;
    public event Action<long>? SourceRequested;
    public event Action<TraceRange>? RangeRequested;
    public int OperationCount => _index?.Calls.Length ?? 0;
    public BindingOperation? SelectedOperation => _selected;
    public string DetailsText => _details.Text ?? "";
    public BindingOperationsControl()
    {
        _requests.Name = "BindingRequestsGrid"; _calls.Name = "BindingCallsGrid";
        AutomationProperties.SetName(_requests, "Binding invocations"); AutomationProperties.SetName(_calls, "Binding expression history and nested calls");
        var panel = new Grid { RowDefinitions = new("Auto,Auto,Auto,2*,5,3*") };
        panel.Children.Add(new TextBlock { Text = "Original calls, values by identity and sentinel kind. Publish may queue delivery; a returned callback is not proof of an effective property value.", FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(10, 8) });
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(8, 0) };
        foreach (var control in new Control[] { _search, _kind, _history }) { control.Margin = new(3); filters.Children.Add(control); }
        Grid.SetRow(filters, 1); panel.Children.Add(filters); Grid.SetRow(_summary, 2); panel.Children.Add(_summary);
        Grid.SetRow(_requests, 3); panel.Children.Add(_requests);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ChartPalette.Alternate }; Grid.SetRow(split, 4); panel.Children.Add(split);
        var detailPanel = new Grid { RowDefinitions = new("Auto,Auto,*,5,*") };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(8, 2) };
        foreach (var button in new[] { _target, _expression, _participant, _property, _source, _range })
        { button.FontSize = 11; button.Padding = new(8, 4); button.Margin = new(2); actions.Children.Add(button); }
        detailPanel.Children.Add(actions); Grid.SetRow(_details, 1); detailPanel.Children.Add(_details);
        var scroll = new ScrollViewer { Content = _chart }; Grid.SetRow(scroll, 2); detailPanel.Children.Add(scroll);
        var innerSplit = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ChartPalette.Alternate }; Grid.SetRow(innerSplit, 3); detailPanel.Children.Add(innerSplit);
        Grid.SetRow(_calls, 4); detailPanel.Children.Add(_calls); Grid.SetRow(detailPanel, 5); panel.Children.Add(detailPanel); Content = panel;
        _search.TextChanged += (_, _) => Refresh(); _kind.SelectionChanged += (_, _) => Refresh();
        _history.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) ShowDetailRows(); };
        _requests.SelectionChanged += (_, _) => { if (!_updating && _requests.SelectedItem is BindingOperation row) { _root = row; ShowDetailRows(); Select(row, true); } };
        _calls.SelectionChanged += (_, _) => { if (!_updating && _calls.SelectedItem is BindingOperation row) Select(row, true); };
        _chart.OperationSelected += row => { _updating = true; try { _calls.SelectedItem = row; _calls.ScrollIntoView(row, null); } finally { _updating = false; } Select(row, true); };
        _target.Click += (_, _) => Inspect(_selected is null ? 0 : _index?.RecordedAttachmentTarget(_selected) ?? 0);
        _expression.Click += (_, _) => Inspect(_selected?.ExpressionId ?? 0); _participant.Click += (_, _) => Inspect(_selected?.ParticipantId ?? 0);
        _property.Click += (_, _) => Inspect(_selected?.PropertyId ?? 0);
        _source.Click += (_, _) => { if (_selected is { } row) SourceRequested?.Invoke(row.EventId); };
        _range.Click += (_, _) => { if (_selected is { IsComplete: true, End: { Duration: > 0 } end }) RangeRequested?.Invoke(new(end.Timestamp, end.End)); };
    }
    private void Inspect(long id) { if (id > 0) ObjectSelected?.Invoke(id); }
    public void SetTrace(TraceView trace, BindingOperationIndex index)
    {
        if (ReferenceEquals(_trace, trace)) return;
        _trace = trace; _index = index; _root = null; _selected = null;
        _summary.Text = index.Calls.Length == 0 ? "No binding observations retained. Enable AVTRACE_BINDING_OPERATIONS=1 on instrumented application output." :
            $"{index.Calls.Length:N0} observed invocations · {index.Roots.Length:N0} execution roots · {index.InvalidMarkers:N0} invalid marker groups · lists limited to {RowLimit:N0} rows; chart to 96";
        Refresh();
    }
    public void SelectEvent(TraceEvent? selected)
    {
        if (_updating || selected is not { } e || !BindingOperationEvent.IsEvent(e) || _index?.Find(BindingOperationEvent.OperationId(e)) is not { } row) return;
        if (_selected?.Id == row.Id) return;
        _root = row; ShowDetailRows(); Select(row, false);
    }
    public void ShowExpressionHistory() { _history.IsChecked = true; ShowDetailRows(); }
    private void Refresh()
    {
        if (_index is null) return;
        var text = _search.Text ?? "";
        var rows = _index.Calls.Where(r => (r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Kind.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)) && (_kind.SelectedIndex switch
        {
            1 => r.Kind is BindingOperationKind.Publish or BindingOperationKind.Deliver or BindingOperationKind.Complete,
            2 => r.Kind is BindingOperationKind.Convert or BindingOperationKind.ConvertBack or BindingOperationKind.WriteSource or BindingOperationKind.UpdateSource or BindingOperationKind.UpdateTarget,
            3 => r.Kind is >= BindingOperationKind.ResourceSubscribe and <= BindingOperationKind.ResolveResource,
            4 => !r.IsComplete || r.Outcome == BindingOperationOutcome.Threw || r.Error is "Binding error" or "Validation error",
            _ => true
        })).Take(RowLimit).ToArray();
        _updating = true;
        try { _requests.ItemsSource = rows; _root = rows.FirstOrDefault(r => r.Id == _root?.Id) ?? rows.FirstOrDefault(); _requests.SelectedItem = _root; }
        finally { _updating = false; }
        ShowDetailRows(); Select(_root, false);
    }
    private void ShowDetailRows()
    {
        var rows = _root is null || _index is null ? [] : _history.IsChecked == true ? _index.History(_root.ExpressionId, RowLimit) : _index.Descendants(_root.Id, RowLimit);
        _updating = true; try { _calls.ItemsSource = rows; _calls.SelectedItem = rows.FirstOrDefault(r => r.Id == _selected?.Id); } finally { _updating = false; }
        _chart.SetOperations(rows, _trace);
    }
    private void Select(BindingOperation? row, bool notify)
    {
        _selected = row; _chart.Select(row?.Id ?? 0);
        var target = row is null ? 0 : _index?.RecordedAttachmentTarget(row) ?? 0;
        _target.IsEnabled = target > 0; _expression.IsEnabled = row?.ExpressionId > 0; _participant.IsEnabled = row?.ParticipantId > 0; _property.IsEnabled = row?.PropertyId > 0;
        _source.IsEnabled = row is not null; _range.IsEnabled = row is { IsComplete: true, End: { Duration: > 0 } };
        _details.Text = row is null ? "Select an invocation" : $"{row.Kind} · {row.Status} · expression #{row.ExpressionId} · {row.Running} · {row.Error}\n" +
            $"Input {row.InputKind} #{row.InputId} → output {row.OutputKind} #{row.OutputId} · {row.Changes}" +
            (target > 0 && row.TargetId == 0 ? $" · earlier successful attachment target #{target} (not current subscription state)" : target > 0 ? $" · target #{target}" : " · target not recorded") +
            (row.CaptureIncomplete ? " · capture incomplete; absent operations are unknown" : "");
        if (notify && row is not null) EventSelected?.Invoke(row.EventId);
    }
}
