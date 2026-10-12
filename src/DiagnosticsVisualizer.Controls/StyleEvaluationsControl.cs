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

/// <summary>Shared observed evaluation/activation inspection. Never evaluates selectors in the viewer.</summary>
public sealed class StyleEvaluationsControl : UserControl
{
    private const int Limit = 8192;
    private readonly TextBox _search = new() { Watermark = "Find selector type, method or outcome…", MaxLength = 512, MinWidth = 160, FontSize = 11 };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Execution roots", "All evaluations", "Selector calls", "Attachment attempts", "Frame activity", "Activation signals" }, SelectedIndex = 0, Width = 170, FontSize = 11 };
    private readonly DataGrid _roots = DiagnosticGrid.Create(("Operation", "Kind", 95), ("Observed result", "Status", 180), ("Subject / identity", "Subject", -1), ("Elapsed ms", "DurationMs", 95), ("Target", "TargetId", 80));
    private readonly DataGrid _calls = CallGrid(), _activity = CallGrid();
    private readonly StyleEvaluationChart _chart = new();
    private readonly TextBlock _summary = new() { Margin = new(10, 5), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Margin = new(10, 5), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _activityNote = new() { Margin = new(10, 5), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TabControl _tabs = new();
    private readonly Dictionary<string, Button> _actions = [];
    private StyleEvaluationIndex? _index;
    private TraceView? _trace;
    private bool _updating;
    private Dictionary<long, StyleEvaluation> _visible = [], _path = [];
    public void ShowActivationHistory() => _tabs.SelectedIndex = 1;
    public int EvaluationCount => _index?.Calls.Length ?? 0;
    public int ActivityCount { get; private set; }
    public StyleEvaluation? SelectedEvaluation => _roots.SelectedItem as StyleEvaluation;
    public StyleEvaluation? CurrentEvaluation => (_tabs.SelectedIndex == 1 ? _activity.SelectedItem : _calls.SelectedItem) as StyleEvaluation ?? SelectedEvaluation;
    public event Action<long>? EventSelected;
    public event Action<long>? ObjectSelected;
    public event Action<long>? SourceRequested;
    public event Action<TraceRange>? RangeRequested;
    private static DataGrid CallGrid() => DiagnosticGrid.Create(("Start ms", "StartMs", 100), ("Step", "Kind", 95), ("Result", "Status", 180), ("Input", "InputDescription", 185),
        ("Activity", "ChangeDescription", 100), ("Elapsed ms", "DurationMs", 95), ("Subject", "SubjectId", 90), ("Target", "TargetId", 90), ("Context", "ContextId", 90), ("Activator", "ActivatorId", 90));
    public StyleEvaluationsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,5,2*") }; root.Children.Add(_summary);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new(10, 4) };
        toolbar.Children.Add(_search); Grid.SetColumn(_mode, 1); toolbar.Children.Add(_mode); Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        Grid.SetRow(_roots, 2); root.Children.Add(_roots); var split = Splitter("Resize styling requests and details"); Grid.SetRow(split, 3); root.Children.Add(split);
        var lower = new Grid { RowDefinitions = new("Auto,Auto,*") }; lower.Children.Add(_detail);
        var actions = new WrapPanel { Margin = new(10, 3), Classes = { "dv-investigation-actions" } };
        ActionButton("Target", r => r.TargetId, ObjectSelectedProxy); ActionButton("Subject", r => r.SubjectId, ObjectSelectedProxy);
        ActionButton("Context", r => r.ContextId, ObjectSelectedProxy); ActionButton("Activator", r => r.ActivatorId, ObjectSelectedProxy);
        ActionButton("Source", r => r.EventId, id => SourceRequested?.Invoke(id));
        var analyze = new Button { Content = "Analyze selected call", FontSize = 11, Margin = new(0, 0, 6, 3) }; _actions["Analyze"] = analyze;
        analyze.Click += (_, _) => { if (CurrentEvaluation is { DurationMs: > 0, Completion: { } span }) RangeRequested?.Invoke(new(span.Timestamp, span.End)); };
        actions.Children.Add(analyze); Grid.SetRow(actions, 1); lower.Children.Add(actions);
        var tree = new Grid { RowDefinitions = new("*,5,*") }; tree.RowDefinitions[0].MinHeight = 48; tree.RowDefinitions[2].MinHeight = 52;
        tree.Children.Add(new ScrollViewer { Content = _chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var treeSplit = Splitter("Resize styling cascade and call table"); Grid.SetRow(treeSplit, 1); tree.Children.Add(treeSplit); Grid.SetRow(_calls, 2); tree.Children.Add(_calls);
        var history = new Grid { RowDefinitions = new("Auto,*") }; history.Children.Add(_activityNote); Grid.SetRow(_activity, 1); history.Children.Add(_activity);
        _tabs.ItemsSource = new[] { new TabItem { Header = "Evaluation tree", Content = tree }, new TabItem { Header = "Related activation history", Content = history } };
        Grid.SetRow(_tabs, 2); lower.Children.Add(_tabs); Grid.SetRow(lower, 4); root.Children.Add(lower); Content = root;
        AutomationProperties.SetName(_roots, "Styling evaluation requests"); AutomationProperties.SetName(_calls, "Styling evaluation calls");
        AutomationProperties.SetName(_activity, "Related style activation history"); AutomationProperties.SetName(_search, "Find style evaluation");
        _search.TextChanged += (_, _) => Refresh(); _mode.SelectionChanged += (_, _) => Refresh();
        _roots.SelectionChanged += (_, _) => { if (!_updating) { ShowDetails(); if (SelectedEvaluation is { } row) EventSelected?.Invoke(row.EventId); } };
        foreach (var grid in new[] { _calls, _activity }) grid.SelectionChanged += (_, _) =>
        { if (!_updating && grid.SelectedItem is StyleEvaluation row) { UpdateActions(); _chart.Select(row.Id); EventSelected?.Invoke(row.EventId); } };
        _tabs.SelectionChanged += (_, _) => UpdateActions();
        _chart.EvaluationSelected += row => { _calls.SelectedItem = row; _calls.ScrollIntoView(row, null); };
        void ObjectSelectedProxy(long id) => ObjectSelected?.Invoke(id);
        void ActionButton(string title, Func<StyleEvaluation, long> identity, Action<long> action)
        {
            var button = new Button { Content = "Inspect " + title.ToLowerInvariant(), FontSize = 11, Margin = new(0, 0, 6, 3) };
            _actions[title] = button; actions.Children.Add(button);
            button.Click += (_, _) => { if (CurrentEvaluation is { } row && identity(row) is > 0 and var id) action(id); };
        }
    }
    public void SetTrace(TraceView trace, StyleEvaluationIndex index)
    {
        if (ReferenceEquals(trace, _trace)) return; _trace = trace; _index = index;
        _summary.Text = index.Calls.Length == 0 ? "No styling observations. Enable AVTRACE_STYLE_EVALUATIONS=1 on instrumented output." :
            $"{index.Roots.Length:N0} execution roots · {index.Calls.Length:N0} evaluations · {index.Calls.Count(r => r.Changed == true):N0} activity changes · {index.InvalidMarkers:N0} invalid" +
            (index.Calls.Any(r => r.CaptureIncomplete) ? " · INCOMPLETE CAPTURE" : "");
        Refresh();
    }
    public void SelectEvent(TraceEvent? selected)
    {
        if (_index is null || selected is not { } e || !StyleEvaluationEvent.IsEvent(e) || _index.Find(StyleEvaluationEvent.EvaluationId(e)) is not { } row) return;
        _updating = true;
        try
        {
            // Preserve the context when an activation-history row is selected; it need not be a subtree child.
            if (_tabs.SelectedIndex == 1 && _activity.ItemsSource?.Cast<StyleEvaluation>().FirstOrDefault(r => r.Id == row.Id) is { } activity)
            { _activity.SelectedItem = activity; UpdateActions(); return; }
            var root = _mode.SelectedIndex == 0 ? _index.RootOf(row.Id) ?? row : row;
            if (_visible.TryGetValue(root.Id, out var visible) && SelectedEvaluation?.Id != root.Id) { _roots.SelectedItem = visible; ShowDetails(); }
            _calls.SelectedItem = _path.GetValueOrDefault(row.Id); _chart.Select(row.Id); UpdateActions();
        }
        finally { _updating = false; }
    }
    private void Refresh()
    {
        var id = SelectedEvaluation?.Id; var text = _search.Text ?? "";
        var rows = (_mode.SelectedIndex == 0 ? _index?.Roots : _index?.Calls) ?? [];
        rows = rows.Where(r => (_mode.SelectedIndex <= 1 || _mode.SelectedIndex == 2 && r.Kind is StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate ||
            _mode.SelectedIndex == 3 && r.Kind == StyleEvaluationKind.Attach || _mode.SelectedIndex == 4 && r.Kind == StyleEvaluationKind.Frame || _mode.SelectedIndex == 5 && r.Kind == StyleEvaluationKind.Signal) &&
            (r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Subject.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Status.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        _updating = true;
        try { _visible = rows.ToDictionary(r => r.Id); _roots.ItemsSource = rows; _roots.SelectedItem = _visible.GetValueOrDefault(id ?? 0) ?? rows.FirstOrDefault(); ShowDetails(); }
        finally { _updating = false; }
    }
    private void ShowDetails()
    {
        var updating = _updating; _updating = true;
        try
        {
            var row = SelectedEvaluation; var path = row is null ? [] : _index!.Descendants(row.Id, Limit + 1);
            var displayed = path.Take(Limit).ToArray(); _path = displayed.ToDictionary(r => r.Call.Id, r => r.Call);
            _calls.ItemsSource = displayed.Select(r => r.Call).ToArray(); _calls.SelectedItem = row;
            var activity = row is null ? [] : _index!.RelatedActivity(row, Limit + 1); ActivityCount = Math.Min(activity.Length, Limit);
            _activity.ItemsSource = activity.Take(Limit).ToArray(); _activity.SelectedItem = null;
            _activityNote.Text = $"{ActivityCount:N0} observations sharing recorded frame/style/activator identities" + (activity.Length > Limit ? " [history display limit reached]" : "") + ". Shared frames need not identify a unique target. No state is extrapolated between observations.";
            _detail.Text = row is null ? "Select a styling operation." : $"{row.Kind} · {row.Status} · {displayed.Length:N0} subtree calls" +
                (path.Length > Limit ? " [table limit reached]" : "") + " · Conditional is not current activity; active is not proof of a winning setter. Identities are not formatted selector expressions.";
            _chart.SetEvaluation(row, displayed, _trace); _chart.Select(row?.Id ?? 0); UpdateActions();
        }
        finally { _updating = updating; }
    }
    private void UpdateActions()
    {
        var row = CurrentEvaluation;
        foreach (var (key, button) in _actions) button.IsEnabled = key switch
        { "Target" => row?.TargetId > 0, "Subject" => row?.SubjectId > 0, "Context" => row?.ContextId > 0, "Activator" => row?.ActivatorId > 0,
            "Source" => row?.EventId > 0, "Analyze" => row?.DurationMs > 0, _ => false };
    }
    private static GridSplitter Splitter(string name)
    {
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ChartPalette.Grid.Brush };
        AutomationProperties.SetName(split, name); return split;
    }
}
