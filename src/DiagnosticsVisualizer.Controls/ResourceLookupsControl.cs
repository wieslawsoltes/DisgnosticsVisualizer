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

/// <summary>Shared searchable recorded lookup trees. Candidate inventory remains a separate inspection view.</summary>
public sealed class ResourceLookupsControl : UserControl
{
    private const int PathLimit = 8192;
    private readonly TextBox _search = new() { Watermark = "Find resource key, theme or provider…", MaxLength = 512, FontSize = 11, MinWidth = 160 };
    private readonly ComboBox _outcome = new() { ItemsSource = new[] { "All lookups", "Found", "Missing", "Threw", "Incomplete / invalid" }, SelectedIndex = 0, Width = 170, FontSize = 11 };
    private readonly CheckBox _allCalls = new() { Content = "All calls", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _roots = DiagnosticGrid.Create(("Resource key", "Key", -1), ("Requested theme", "Theme", 140),
        ("Outcome", "Status", 160), ("Elapsed ms", "DurationMs", 105), ("Lookup provider", "Provider", 260));
    private readonly DataGrid _calls = DiagnosticGrid.Create(("Provider", "Provider", -1), ("Key", "Key", 180), ("Result", "Status", 150),
        ("Elapsed ms", "DurationMs", 105), ("Returned type / identity", "ResultType", 240));
    private readonly ResourceLookupChart _chart = new();
    private readonly TextBlock _summary = new() { Margin = new(10, 5), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Margin = new(10, 6), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap };
    private ResourceLookupIndex? _index;
    private TraceView? _trace;
    private bool _updating;
    private Dictionary<long, ResourceLookup> _visibleRoots = [], _visibleCalls = [];
    public ResourceLookup? SelectedLookup => _roots.SelectedItem as ResourceLookup;
    public int LookupCount => _index?.Calls.Length ?? 0;
    public event Action<long>? EventSelected;
    public event Action<long>? ObjectSelected;
    public event Action<TraceRange>? RangeRequested;
    public ResourceLookupsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,5,2*") }; root.Children.Add(_summary);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Margin = new(10, 4) };
        toolbar.Children.Add(_search); Grid.SetColumn(_outcome, 1); toolbar.Children.Add(_outcome); Grid.SetColumn(_allCalls, 2); toolbar.Children.Add(_allCalls);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar); Grid.SetRow(_roots, 2); root.Children.Add(_roots);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ChartPalette.Grid.Brush };
        Grid.SetRow(splitter, 3); root.Children.Add(splitter);
        var lower = new Grid { RowDefinitions = new("Auto,Auto,130,*") }; lower.Children.Add(_detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(10, 3), Spacing = 8 };
        var inspect = new Button { Content = "Inspect provider", FontSize = 11 };
        inspect.Click += (_, _) => { if ((_calls.SelectedItem as ResourceLookup ?? SelectedLookup) is { ProviderId: > 0 } r) ObjectSelected?.Invoke(r.ProviderId); };
        var range = new Button { Content = "Analyze lookup", FontSize = 11 };
        range.Click += (_, _) => { if (SelectedLookup is { DurationMs: not null, Completion: { } e } && e.End > e.Timestamp) RangeRequested?.Invoke(new(e.Timestamp, e.End)); };
        actions.Children.Add(inspect); actions.Children.Add(range); Grid.SetRow(actions, 1); lower.Children.Add(actions);
        var scroll = new ScrollViewer { Content = _chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2); lower.Children.Add(scroll); Grid.SetRow(_calls, 3); lower.Children.Add(_calls);
        Grid.SetRow(lower, 4); root.Children.Add(lower); Content = root;
        AutomationProperties.SetName(_roots, "Resource lookup requests"); AutomationProperties.SetName(_calls, "Resource lookup provider calls"); AutomationProperties.SetName(_search, "Find resource lookup");
        _search.TextChanged += (_, _) => Refresh(); _outcome.SelectionChanged += (_, _) => Refresh();
        _allCalls.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) Refresh(); };
        _roots.SelectionChanged += (_, _) => { if (!_updating) { ShowCalls(); if (SelectedLookup is { } row) EventSelected?.Invoke(row.EventId); } };
        _calls.SelectionChanged += (_, _) => { if (!_updating && _calls.SelectedItem is ResourceLookup row) { _chart.Select(row.Id); EventSelected?.Invoke(row.EventId); } };
        _chart.LookupSelected += row => { _calls.SelectedItem = row; _calls.ScrollIntoView(row, null); };
    }
    public void SetTrace(TraceView trace, ResourceLookupIndex index)
    {
        if (ReferenceEquals(_trace, trace)) return; _trace = trace; _index = index;
        _summary.Text = index.Calls.Length == 0 ? "No resource lookup observations. Enable AVTRACE_RESOURCE_LOOKUPS=1 on instrumented output." :
            $"{index.Roots.Length:N0} root requests · {index.Calls.Length:N0} provider calls · {index.Roots.Count(r => r.Status == "Missing"):N0} missing · {index.InvalidMarkers:N0} invalid" +
            (index.Calls.Any(c => c.CaptureIncomplete) ? " · INCOMPLETE: unobserved providers remain unknown" : "");
        Refresh();
    }
    public void SelectEvent(TraceEvent? selected)
    {
        if (_index is null || selected is not { } e || !ResourceLookupEvent.IsEvent(e)) return;
        var row = _index.Find(ResourceLookupEvent.LookupId(e)); if (row is null) return;
        var root = _allCalls.IsChecked == true ? row : _index.RootOf(row.Id) ?? row;
        _updating = true;
        try
        {
            if (_visibleRoots.GetValueOrDefault(root.Id) is { } visible && SelectedLookup?.Id != root.Id)
            { _roots.SelectedItem = visible; ShowCalls(); }
            _calls.SelectedItem = _visibleCalls.GetValueOrDefault(row.Id); _chart.Select(row.Id);
        }
        finally { _updating = false; }
    }
    private void Refresh()
    {
        var id = SelectedLookup?.Id; var text = _search.Text ?? "";
        var rows = (_allCalls.IsChecked == true ? _index?.Calls : _index?.Roots) ?? [];
        rows = rows.Where(r => (r.Key.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Theme.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Provider.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
            (_outcome.SelectedIndex <= 0 || _outcome.SelectedIndex == 1 && r.Status.StartsWith("Found", StringComparison.Ordinal) ||
             _outcome.SelectedIndex == 2 && r.Status == "Missing" || _outcome.SelectedIndex == 3 && r.Status == "Threw" || _outcome.SelectedIndex == 4 && !r.IsComplete)).ToArray();
        _updating = true;
        try { _visibleRoots = rows.ToDictionary(r => r.Id); _roots.ItemsSource = rows; _roots.SelectedItem = rows.FirstOrDefault(r => r.Id == id) ?? rows.FirstOrDefault(); ShowCalls(); }
        finally { _updating = false; }
    }
    private void ShowCalls()
    {
        var updating = _updating; _updating = true;
        try
        {
            var row = SelectedLookup; var path = row is null ? [] : _index!.Descendants(row.Id, PathLimit + 1);
            var displayed = path.Take(PathLimit).ToArray(); _visibleCalls = displayed.ToDictionary(r => r.Call.Id, r => r.Call); _calls.ItemsSource = displayed.Select(r => r.Call).ToArray();
            _detail.Text = row is null ? "Select a request to inspect the actual call path." :
                $"{row.Key} (key #{row.KeyId}) · {row.Theme} · {row.Status} · {displayed.Length:N0} observed calls" + (path.Length > PathLimit ? " [table limit reached]" : "") +
                ". Found means this call returned true; a found null is distinct from a miss. Provider order is observed, not inferred. Resource values are never formatted.";
            _chart.SetLookup(row, displayed, _trace);
        }
        finally { _updating = updating; }
    }
}
