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

/// <summary>Setter attempts and original notifications with independently private values.</summary>
public sealed class PropertyHistoryControl : UserControl
{
    private const int Limit = 8192;
    private readonly TextBox _search = new() { Watermark = "Find property or operation…", MinWidth = 180, MaxLength = 512, FontSize = 11 };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "All property operations", "Effective notifications", "Base notifications", "Setter attempts", "Errors / incomplete" }, SelectedIndex = 0, Width = 200, FontSize = 11 };
    private readonly CheckBox _history = new() { Content = "Selected property history", FontSize = 11 };
    private readonly TextBlock _summary = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(10, 6) };
    private readonly TextBlock _details = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(10, 5) };
    private readonly DataGrid _requests = DiagnosticGrid.Create(("Property", "Property", 175), ("Object", "TargetId", 90), ("Observation", "Category", 175), ("Priority", "PriorityText", 125), ("Outcome", "Status", 115), ("New / requested", "AfterText", -1));
    private readonly DataGrid _calls = DiagnosticGrid.Create(("Property", "Property", 150), ("Observation", "Category", 175), ("Before", "BeforeText", 140), ("New / requested", "AfterText", 160), ("Priority", "PriorityText", 110), ("Outcome", "Status", 100), ("Duration ms", "DurationMs", 100), ("Site", "Name", -1));
    private readonly PropertyValueChart _chart = new();
    private readonly Button _target = new() { Content = "Inspect target" }, _property = new() { Content = "Property identity" }, _participant = new() { Content = "Value entry / setter" }, _source = new() { Content = "Source" }, _range = new() { Content = "Analyze call" };
    private PropertyOperationIndex? _index; private TraceView? _trace; private PropertyOperation? _selected, _root; private bool _updating;
    public event Action<long>? EventSelected;
    public event Action<long>? ObjectSelected;
    public event Action<long>? SourceRequested;
    public event Action<TraceRange>? RangeRequested;
    public int OperationCount => _index?.Calls.Length ?? 0;
    public PropertyOperation? SelectedOperation => _selected;
    public string DetailsText => _details.Text ?? "";
    public PropertyHistoryControl()
    {
        _requests.Name = "PropertyRequestsGrid"; _calls.Name = "PropertyHistoryGrid";
        AutomationProperties.SetName(_requests, "Property operations"); AutomationProperties.SetName(_calls, "Property chronological history");
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,2*,5,3*") };
        root.Children.Add(new TextBlock { Text = "Setter return does not prove an effective value. Notification values are captured at entry, before reentrant observers; no getter is replayed.", FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(10, 8) });
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(8, 0) };
        foreach (var control in new Control[] { _search, _kind, _history }) { control.Margin = new(3); filters.Children.Add(control); }
        Grid.SetRow(filters, 1); root.Children.Add(filters); Grid.SetRow(_summary, 2); root.Children.Add(_summary); Grid.SetRow(_requests, 3); root.Children.Add(_requests);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(split, 4); root.Children.Add(split);
        var detail = new Grid { RowDefinitions = new("Auto,Auto,180,5,*") };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(8, 2) };
        foreach (var b in new[] { _target, _property, _participant, _source, _range }) { b.FontSize = 11; b.Margin = new(2); b.Padding = new(8, 4); actions.Children.Add(b); }
        detail.Children.Add(actions); Grid.SetRow(_details, 1); detail.Children.Add(_details); Grid.SetRow(_chart, 2); detail.Children.Add(_chart);
        var inner = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(inner, 3); detail.Children.Add(inner);
        Grid.SetRow(_calls, 4); detail.Children.Add(_calls); Grid.SetRow(detail, 5); root.Children.Add(detail); Content = root;
        _search.TextChanged += (_, _) => Refresh(); _kind.SelectionChanged += (_, _) => Refresh();
        _history.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) DetailRows(); };
        _requests.SelectionChanged += (_, _) => { if (!_updating && _requests.SelectedItem is PropertyOperation r) { _root = r; DetailRows(); Select(r, true); } };
        _calls.SelectionChanged += (_, _) => { if (!_updating && _calls.SelectedItem is PropertyOperation r) Select(r, true); };
        _chart.OperationSelected += r => { _updating = true; try { _calls.SelectedItem = r; _calls.ScrollIntoView(r, null); } finally { _updating = false; } Select(r, true); };
        _target.Click += (_, _) => Inspect(_selected?.TargetId ?? 0); _property.Click += (_, _) => Inspect(_selected?.PropertyId ?? 0); _participant.Click += (_, _) => Inspect(_selected?.ParticipantId ?? 0);
        _source.Click += (_, _) => { if (_selected is { } r) SourceRequested?.Invoke(r.EventId); };
        _range.Click += (_, _) => { if (_selected is { IsComplete: true, End: { Duration: > 0 } e }) RangeRequested?.Invoke(new(e.Timestamp, e.End)); };
    }
    private void Inspect(long id) { if (id > 0) ObjectSelected?.Invoke(id); }
    public void SetTrace(TraceView trace, PropertyOperationIndex index)
    {
        if (ReferenceEquals(_trace, trace)) return;
        var selected = _trace?.SessionId == trace.SessionId ? _selected?.Id : null;
        _trace = trace; _index = index; _root = selected is { } id ? index.Find(id) : null;
        _summary.Text = $"{index.Calls.Length:N0} operations · {index.Calls.Count(r => r.Effective):N0} effective / {index.Calls.Count(r => r.BaseValue):N0} base notifications · {index.InvalidMarkers:N0} invalid groups · tables limited to {Limit:N0}";
        if (index.Calls.Length == 0) _summary.Text = "No property history. Enable AVTRACE_PROPERTY_OPERATIONS=1; primitive payloads additionally require AVTRACE_PROPERTY_SCALARS=1.";
        Refresh();
    }
    public void SelectEvent(TraceEvent? e)
    {
        if (_updating || e is not { } selected || !PropertyOperationEvent.IsEvent(selected) || _index?.Find(PropertyOperationEvent.OperationId(selected)) is not { } row || row.Id == _selected?.Id) return;
        _root = row; DetailRows(); Select(row, false);
    }
    public void ShowHistory() { _history.IsChecked = true; DetailRows(); }
    private void Refresh()
    {
        if (_index is null) return;
        var text = _search.Text ?? "";
        var rows = _index.Calls.Where(r => (r.Property.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) && (_kind.SelectedIndex switch
        { 1 => r.Effective, 2 => r.BaseValue, 3 => r.Kind != PropertyOperationKind.Notify, 4 => !r.IsComplete || r.Status == "Threw", _ => true })).Take(Limit).ToArray();
        _updating = true;
        try { _requests.ItemsSource = rows; _root = rows.FirstOrDefault(r => r.Id == _root?.Id) ?? rows.FirstOrDefault(); _requests.SelectedItem = _root; }
        finally { _updating = false; }
        DetailRows(); Select(_root, false);
    }
    private void DetailRows()
    {
        var rows = _root is null || _index is null ? [] : _history.IsChecked == true ? _index.History(_root.TargetId, _root.PropertyId, Limit) : _index.Descendants(_root.Id, Limit);
        _updating = true; try { _calls.ItemsSource = rows; _calls.SelectedItem = rows.FirstOrDefault(r => r.Id == _selected?.Id); } finally { _updating = false; }
        _chart.SetOperations(rows, _trace);
    }
    private void Select(PropertyOperation? row, bool notify)
    {
        _selected = row; _chart.Select(row?.Id ?? 0); _target.IsEnabled = row?.TargetId > 0; _property.IsEnabled = row?.PropertyId > 0; _participant.IsEnabled = row?.ParticipantId > 0;
        _source.IsEnabled = row is not null; _range.IsEnabled = row is { IsComplete: true, End: { Duration: > 0 } };
        _details.Text = row is null ? "Select an operation" : $"{row.PropertyKind} {row.Property} · target #{row.TargetId} · {row.Category} · {row.Status} · {row.PriorityText}\n" +
            $"Before: {row.BeforeText} → {(row.Kind == PropertyOperationKind.Notify ? "notified" : "requested/configured")}: {row.AfterText}" +
            (row.Kind == PropertyOperationKind.Notify ? " · observer completion is not the current state" : " · return is not proof of a committed value") + (row.CaptureIncomplete ? " · incomplete capture" : "");
        if (notify && row is not null) EventSelected?.Invoke(row.EventId);
    }
}
