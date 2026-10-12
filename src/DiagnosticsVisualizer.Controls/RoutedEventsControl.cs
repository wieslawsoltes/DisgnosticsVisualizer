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

public sealed class RoutedEventsControl : UserControl
{
    private readonly TextBox _search = new() { Watermark = "Find routed event…", FontSize = 11, MaxLength = 512, MinWidth = 150 };
    private readonly ComboBox _outcomes = new() { ItemsSource = new[] { "All dispatches", "Handled changed", "Threw", "Incomplete / invalid" }, SelectedIndex = 0, FontSize = 11, Width = 160 };
    private readonly CheckBox _classes = new() { Content = "Class listeners", IsChecked = true, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { FontSize = 11, Margin = new(10, 5), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { FontSize = 10, Margin = new(10, 6), TextWrapping = TextWrapping.Wrap, Foreground = ChartPalette.Muted };
    private readonly DataGrid _dispatches = DiagnosticGrid.Create(("Routed event", "Name", -1), ("Source", "SourceId", 75), ("Routes", "Routes", 130),
        ("Outcome", "Status", 130), ("Elapsed ms", "DurationMs", 100), ("Handlers", "Invoked", 80), ("Skipped", "Skipped", 80), ("Handled", "HandledAfter", 80));
    private readonly DataGrid _steps = DiagnosticGrid.Create(("Phase", "Phase", 80), ("Target", "ObjectId", 75), ("Handler / notification", "Name", -1),
        ("Outcome", "Status", 145), ("Elapsed ms", "DurationMs", 100), ("Before", "HandledBefore", 80), ("After", "HandledAfter", 80), ("Handled too", "HandledEventsToo", 100));
    private readonly RoutedRouteChart _chart = new();
    private TraceView? _trace;
    private RoutedDispatch[] _rows = [];
    private Dictionary<long, RoutedDispatch> _byId = [];
    private bool _updating;
    public int DispatchCount => _rows.Length;
    public RoutedDispatch? SelectedDispatch => _dispatches.SelectedItem as RoutedDispatch;
    public event Action<long>? EventSelected;
    public event Action<long>? ObjectSelected;
    public event Action<TraceRange>? RangeRequested;
    public RoutedEventsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,2*,5,3*") };
        root.Children.Add(_summary);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Margin = new(10, 4) };
        toolbar.Children.Add(_search); Grid.SetColumn(_outcomes, 1); toolbar.Children.Add(_outcomes); Grid.SetColumn(_classes, 2); toolbar.Children.Add(_classes);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar); Grid.SetRow(_dispatches, 2); root.Children.Add(_dispatches);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ChartPalette.Grid.Brush };
        Grid.SetRow(split, 3); root.Children.Add(split);
        var details = new Grid { RowDefinitions = new("Auto,Auto,125,*") }; details.Children.Add(_detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(10, 3) };
        var inspect = new Button { Content = "Inspect target", FontSize = 11 };
        inspect.Click += (_, _) => { if (_steps.SelectedItem is RoutedStep s) ObjectSelected?.Invoke(s.ObjectId); };
        var range = new Button { Content = "Analyze dispatch", FontSize = 11 };
        range.Click += (_, _) => { if (SelectedDispatch is { Completion: { } c, DurationMs: not null } && c.End > c.Timestamp) RangeRequested?.Invoke(new(c.Timestamp, c.End)); };
        actions.Children.Add(inspect); actions.Children.Add(range); Grid.SetRow(actions, 1); details.Children.Add(actions);
        var scroll = new ScrollViewer { Content = _chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2); details.Children.Add(scroll); Grid.SetRow(_steps, 3); details.Children.Add(_steps);
        Grid.SetRow(details, 4); root.Children.Add(details); Content = root;
        AutomationProperties.SetName(_dispatches, "Routed dispatches"); AutomationProperties.SetName(_steps, "Routed handler steps");
        AutomationProperties.SetName(_search, "Find routed event");
        _search.TextChanged += (_, _) => Refresh(); _outcomes.SelectionChanged += (_, _) => Refresh();
        _classes.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) ShowSteps(); };
        _dispatches.SelectionChanged += (_, _) => { if (!_updating) { ShowSteps(); if (SelectedDispatch is { } r) EventSelected?.Invoke(r.EventId); } };
        _steps.SelectionChanged += (_, _) => { if (!_updating && _steps.SelectedItem is RoutedStep s) { _chart.Select(s.EventId); EventSelected?.Invoke(s.EventId); } };
        _chart.StepSelected += s => { _steps.SelectedItem = s; _steps.ScrollIntoView(s, null); };
    }
    public void SetTrace(TraceView trace, RoutedDispatch[] rows)
    {
        if (ReferenceEquals(trace, _trace)) return;
        _trace = trace; _rows = rows; _byId = rows.ToDictionary(r => r.Id);
        _summary.Text = rows.Length == 0 ? "No routed dispatch data. Enable AVTRACE_ROUTED_EVENTS=1 on instrumented output." :
            $"{rows.Length:N0} observed dispatches · {rows.Sum(r => r.Invoked):N0} instance-handler invocations · {rows.Sum(r => r.ChangedHandled):N0} handled transitions" +
            (rows.Any(r => r.CaptureIncomplete) ? " · INCOMPLETE CAPTURE: missing steps are unknown" : "");
        Refresh();
    }
    public void SelectEvent(TraceEvent? selected)
    {
        if (selected is not { } e || !RoutedDispatchEvent.IsEvent(e) || !_byId.TryGetValue(e.Data1, out var row)) return;
        _updating = true;
        try
        {
            if (!ReferenceEquals(SelectedDispatch, row) && (_dispatches.ItemsSource?.Cast<RoutedDispatch>().Contains(row) ?? false)) { _dispatches.SelectedItem = row; ShowSteps(); }
            if (ReferenceEquals(SelectedDispatch, row)) { _steps.SelectedItem = row.Steps.FirstOrDefault(s => s.EventId == e.Id); _chart.Select(e.Id); }
        }
        finally { _updating = false; }
    }
    private void Refresh()
    {
        var id = SelectedDispatch?.Id;
        var rows = _rows.Where(r => r.Name.Contains(_search.Text ?? "", StringComparison.OrdinalIgnoreCase) &&
            (_outcomes.SelectedIndex <= 0 || _outcomes.SelectedIndex == 1 && r.ChangedHandled > 0 || _outcomes.SelectedIndex == 2 && r.Status == "Threw" ||
             _outcomes.SelectedIndex == 3 && r.Status is not ("Returned" or "Threw"))).ToArray();
        _updating = true;
        try { _dispatches.ItemsSource = rows; _dispatches.SelectedItem = rows.FirstOrDefault(r => r.Id == id) ?? rows.FirstOrDefault(); ShowSteps(); }
        finally { _updating = false; }
    }
    private void ShowSteps()
    {
        var updating = _updating; _updating = true;
        try
        {
            var row = SelectedDispatch; var selected = (_steps.SelectedItem as RoutedStep)?.EventId;
            var steps = (row?.Steps ?? []).Where(s => _classes.IsChecked == true || s.Kind != "Class listeners").ToArray();
            _steps.ItemsSource = steps; _steps.SelectedItem = steps.FirstOrDefault(s => s.EventId == selected);
            _detail.Text = row is null ? "Select a dispatch to inspect propagation." :
                $"{row.Name} · source #{row.SourceId} · routed event #{row.RoutedEventId} · {row.Status}. Class-listener and multicast invocations are aggregate boundaries, not individual subscriber timings.";
            _chart.SetDispatch(row, steps, _trace);
        }
        finally { _updating = updating; }
    }
}
