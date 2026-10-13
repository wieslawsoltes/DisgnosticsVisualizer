using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Workbench.Docking;

namespace DiagnosticsVisualizer.Workbench;

/// <summary>Shared desktop/WebAssembly workbench; platform-specific entry points supply only a TopLevel.</summary>
public sealed partial class WorkbenchView : UserControl, IDisposable
{
    private readonly WorkbenchModel _model;
    private readonly TimelineControl _timeline = new();
    private readonly TimelineControl _flame = new() { Grouping = TimelineGrouping.ThreadFlame };
    private readonly CausalityGraph _causal = new();
    private readonly SubsystemMap _systems = new();
    private readonly CostChart _costChart = new();
    private readonly TreeExplorer _visual = new(), _logical = new(true);
    private readonly PropertyInspector _properties = new();
    private readonly EventDetailsControl _details = new();
    private readonly DataGrid _events = DiagnosticGrid.Create(("Start ms", "StartMs", 88), ("Duration ms", "DurationMs", 90), ("Subsystem", "Subsystem", 105), ("Operation", "Name", -1), ("Thread", "ThreadId", 62), ("Object", "ObjectId", 66));
    private readonly DataGrid _findings = DiagnosticGrid.Create(("Severity", "Severity", 78), ("Finding", "Title", 250), ("Evidence and caveats", "Explanation", -1), ("Inferred", "Inferred", 70));
    private readonly DataGrid _costs = DiagnosticGrid.Create(("Operation", "Name", -1), ("Count", "Count", 70), ("Self ms", "SelfMs", 88), ("Total ms", "TotalMs", 90), ("Max ms", "MaximumMs", 85));
    private readonly DataGrid _comparison = DiagnosticGrid.Create(("Operation", "Name", -1), ("Before ms", "BaselineSelfMs", 95), ("After ms", "CurrentSelfMs", 95), ("Delta ms", "DeltaMs", 95), ("Change %", "Percent", 85));
    private readonly TextBox _filter = new() { Watermark = "Query: subsystem:Layout duration>1ms", Width = 300, FontSize = 11, Height = 30 };
    private readonly ComboBox _category = new() { Width = 155, FontSize = 11, MinHeight = 30 };
    private readonly CheckBox _objectOnly = new() { Content = "Selected object", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _source = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ChartPalette.Muted };
    private readonly TextBlock _status = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _count = MetricValue(), _duration = MetricValue(), _threadCount = MetricValue(), _nodeCount = MetricValue(), _loss = MetricValue();
    private readonly TextBlock _error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FFC5C5")) };
    private readonly Border _errorBanner;
    private readonly Border _overlay;
    private readonly Grid _root;
    private readonly DockControl _dock = new();
    private readonly WorkbenchDockFactory _factory;
    private IRootDock _layout;
    private readonly Button _record, _connect;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly List<(string Name, string Shortcut, Func<Task> Run)> _commands = [];
    private bool _updating, _disposed;
    private readonly SourceExplorer _sources = new();
    private readonly AsyncOperationsControl _asyncOperations = new();
    private readonly DependencyExplorer _dependencies = new();
    public void ShowDependencies() => _factory.Activate("dependencies");
    public void ShowAsyncOperations() => _factory.Activate("async");
    public void ShowSources() => _factory.Activate("sources");
    public WorkbenchModel Model => _model;
    public WorkbenchView() : this(new WorkbenchModel()) { }
    public WorkbenchView(WorkbenchModel model)
    {
        _model = model; MinWidth = 760; MinHeight = 440; Focusable = true;
        Background = new SolidColorBrush(Color.Parse("#1F2124"));
        _root = new Grid { RowDefinitions = new("40,44,Auto,Auto,Auto,*,24") };
        _record = ToolbarButton("Record", "M8 1A7 7 0 1 0 8 15A7 7 0 1 0 8 1Z", () => RunAsync(_model.ToggleRecordingAsync));
        _record.Classes.Add("dv-primary");
        _connect = ToolbarButton("Connect", "M5 2V6M11 2V6M3 6H13V8A5 5 0 0 1 8 13V16M5 6H11", async () =>
        { if (_model.Connected) await RunAsync(_model.DisconnectAsync); else ShowConnection(); });
        var header = BuildHeader(); _root.Children.Add(header);
        var metrics = BuildMetrics(); Grid.SetRow(metrics, 1); _root.Children.Add(metrics);
        _errorBanner = BuildErrorBanner(); Grid.SetRow(_errorBanner, 2); _root.Children.Add(_errorBanner);
        _category.ItemsSource = new[] { "All subsystems" }.Concat(Enum.GetNames<Subsystem>()).ToArray(); _category.SelectedIndex = 0;
        var views = new Dictionary<string, Control>
        {
            ["native"] = _nativeCapture, ["timeline"] = ChartPanel(_timeline, _timeline.Fit), ["flame"] = ChartPanel(_flame, _flame.Fit),
            ["causality"] = ChartPanel(_causal, _causal.Fit), ["systems"] = new ScrollViewer { Content = _systems },
            ["comparison"] = ComparisonPanel(), ["visual"] = TreePanel(_visual), ["logical"] = TreePanel(_logical),
            ["routed"] = _routed, ["styling"] = _styleEvaluations, ["resources"] = _resourceLookups, ["dispatcher"] = _dispatcher, ["xaml"] = _xaml, ["dependencies"] = _dependencies, ["async"] = _asyncOperations, ["sources"] = _sources, ["properties"] = _properties, ["details"] = _details, ["events"] = EventPanel(),
            ["findings"] = _findings, ["costs"] = CostPanel(),
            ["topdown"] = _topDown, ["bottomup"] = _bottomUp, ["statistics"] = _statistics, ["threads"] = _threads,
            ["scene"] = _scene, ["history"] = HistoryPanel(), ["bookmarks"] = _bookmarks,
            ["health"] = new ScrollViewer { Content = _captureHealth }
        };
        _factory = new(views); _layout = _factory.CreateLayout(); _factory.InitLayout(_layout); _dock.Layout = _layout;
        var dockArea = BuildDockArea(); Grid.SetRow(dockArea, 5); _root.Children.Add(dockArea);
        Grid.SetRow(_overview, 3); _root.Children.Add(_overview);
        var rangeBar = RangeBar(); Grid.SetRow(rangeBar, 4); _root.Children.Add(rangeBar);
        var footer = BuildFooter(); Grid.SetRow(footer, 6); _root.Children.Add(footer);
        _overlay = new Border { Background = new SolidColorBrush(Color.Parse("#AA070B12")), IsVisible = false, ZIndex = 1000 };
        Grid.SetRowSpan(_overlay, 7); _root.Children.Add(_overlay); Content = WorkbenchStyles.CreateScope(_root);
        WireSelection(); RegisterCommands(); RegisterProfilingCommands(); InitializeInvestigation();
        _commands.Add(("Show Sources", "F12", () => { _factory.Activate("sources"); return Task.CompletedTask; }));
        _details.SourceRequested += () => _factory.Activate("sources");
        _commands.Add(("Show Async operations", "", () => { _factory.Activate("async"); return Task.CompletedTask; }));
        _commands.Add(("Show Dependencies", "", () => { ShowDependencies(); return Task.CompletedTask; }));
        _dependencies.ObjectSelected += id => _model.SelectObject(id, false);
        _asyncOperations.EventSelected += id => _model.SelectEvent(id, false);
        _asyncOperations.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        InitializeProfiling(); BindPanelRails();
        _model.TraceUpdated += OnTraceUpdated; _model.SelectionChanged += OnSelectionChanged; _model.PropertyChanged += ModelChanged;
        _model.ComparisonUpdated += OnComparisonUpdated;
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RefreshEventRows(); };
        _filter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        _category.SelectionChanged += (_, _) => RefreshEventRows();
        _objectOnly.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) RefreshEventRows(); };
        KeyDown += HandleKeyDown;
        if (_model.Trace is { } trace && _model.Analysis is { } analysis) OnTraceUpdated(trace, analysis);
        OnSelectionChanged(_model.Trace?.Events.FirstOrDefault(e => e.Id == _model.SelectedEventId), _model.SelectedObjectId, false); UpdateChrome();
    }
    private void WireSelection()
    {
        _timeline.EventSelected += selected => { _model.SelectEvent(selected.Id, false); _factory.Activate("details"); };
        _flame.EventSelected += selected => { _model.SelectEvent(selected.Id, false); _factory.Activate("details"); };
        _causal.EventSelected += selected => _model.SelectEvent(selected.Id, false);
        _visual.ObjectSelected += id => { _model.SelectObject(id); _factory.Activate("properties"); };
        _logical.ObjectSelected += id => { _model.SelectObject(id); _factory.Activate("properties"); };
        _properties.ObjectSelected += id => _model.SelectObject(id);
        _details.ParentRequested += id => _model.SelectEvent(id, true); _details.ObjectRequested += id => { _model.SelectObject(id); _factory.Activate("properties"); };
        _events.SelectionChanged += (_, _) => { if (!_updating && _events.SelectedItem is EventRow row) _model.SelectEvent(row.Id, false); };
        _events.DoubleTapped += (_, _) => { if (_events.SelectedItem is EventRow row) _model.SelectEvent(row.Id, true); };
        _findings.SelectionChanged += (_, _) => { if (!_updating && _findings.SelectedItem is Finding finding && finding.EventId != 0) _model.SelectEvent(finding.EventId, true); };
        _systems.SubsystemSelected += category => { _category.SelectedIndex = (int)category + 1; _factory.Activate("events"); };
        _costChart.CostSelected += cost => { _filter.Text = "name=" + System.Text.Json.JsonSerializer.Serialize(cost.Name); _factory.Activate("events"); };
        _costs.DoubleTapped += (_, _) => { if (_costs.SelectedItem is CostRow cost) { _filter.Text = "name=" + System.Text.Json.JsonSerializer.Serialize(cost.Name); _factory.Activate("events"); } };
    }
    private void OnTraceUpdated(TraceView trace, AnalysisResult analysis)
    {
        _updating = true;
        try
        {
            _timeline.Trace = trace; _flame.Trace = trace; _causal.SetTrace(trace, analysis, _model.SelectedEventId); _systems.SetTrace(trace, analysis);
            _visual.SetSnapshot(trace.Snapshot); _logical.SetSnapshot(trace.Snapshot); _costChart.Rows = analysis.Costs;
            _asyncOperations.SetOperations(analysis.AsyncOperations, trace); UpdateInvestigation(trace, analysis);
            _costs.ItemsSource = analysis.Costs; _findings.ItemsSource = analysis.Findings;
            _count.Text = trace.Events.Length.ToString("N0");
            _duration.Text = trace.Events.Length == 0 ? "0 s" : $"{trace.Milliseconds(trace.Events.Max(e => e.End) - trace.Events.Min(e => e.Timestamp)) / 1000:F2} s";
            _threadCount.Text = trace.Events.Select(e => e.ThreadId).Distinct().Count().ToString(); _nodeCount.Text = trace.Snapshot.Nodes.Length.ToString("N0");
            _loss.Text = trace.DroppedEvents.ToString("N0"); _loss.Foreground = trace.DroppedEvents > 0 ? new SolidColorBrush(Color.Parse("#F5B680")) : ChartPalette.Foreground;
            ToolTip.SetTip(_loss, $"{trace.MissingBlocks:N0} missing batches · {trace.ProbeFaults:N0} probe faults · {trace.MetadataDrops:N0} metadata drops · {trace.EvictedEvents:N0} events evicted from viewer history");
            RefreshEventRows(); UpdateProfilingTrace(trace);
        }
        finally { _updating = false; }
        UpdateChrome();
    }
    private void OnSelectionChanged(TraceEvent? selected, long objectId, bool focus)
    {
        if (_model.Trace is not { } trace) return;
        _timeline.SelectedEventId = _flame.SelectedEventId = selected?.Id ?? 0;
        _timeline.SelectedObjectId = _flame.SelectedObjectId = objectId;
        _visual.SelectObject(objectId, focus); _logical.SelectObject(objectId, focus);
        _properties.Inspect(_historicalSnapshot is null ? trace : trace with { Snapshot = _historicalSnapshot }, objectId); _details.Inspect(trace, selected);
        _scene.SetSnapshot(_historicalSnapshot ?? trace.Snapshot, objectId);
        _dependencies.Inspect(trace, objectId, _historicalSnapshot);
        SelectInvestigation(trace, selected, objectId);
        if (selected is { } e)
        {
            _causal.SelectEvent(e.Id);
            if (focus) { _timeline.FocusEvent(e); _flame.FocusEvent(e); _factory.Activate("timeline"); }
        }
        if (_objectOnly.IsChecked == true) RefreshEventRows();
    }
    private void OnComparisonUpdated() { _comparison.ItemsSource = _model.Comparison; if (_model.Comparison.Length > 0) _factory.Activate("comparison"); }
    private void RefreshEventRows()
    {
        var updating = _updating; _updating = true;
        try
        {
            var rows = _model.FilterEvents(_filter.Text, _category.SelectedIndex <= 0 ? null : (Subsystem)(_category.SelectedIndex - 1), _objectOnly.IsChecked == true);
            _queryError.Text = _model.QueryError; _queryError.IsVisible = _model.QueryError.Length != 0;
            _events.ItemsSource = rows; _events.SelectedItem = rows.FirstOrDefault(row => row.Id == _model.SelectedEventId);
        }
        finally { _updating = updating; }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateChrome();
    private void UpdateChrome()
    {
        _navigateBack.IsEnabled = _model.CanNavigateBack; _navigateForward.IsEnabled = _model.CanNavigateForward;
        _source.Text = _model.Source; _status.Text = _model.Status;
        _error.Text = _model.Error; _errorBanner.IsVisible = !string.IsNullOrEmpty(_model.Error);
        _record.Content = _model.Recording && _model.Connected ? "Pause capture" : "Record"; _record.IsEnabled = _model.Connected && _model.SupportsRecordingControl;
        _connect.Content = _model.Connected ? "Disconnect" : "Connect";
    }
    public void Dispose()
    {
        if (_disposed) return; UnbindPanelRails(); SaveWorkspace(); _disposed = true; _filterTimer.Stop(); _workspaceTimer.Stop();
        _model.ProfileUpdated -= ProfileChanged; _model.BookmarksUpdated -= BookmarksChanged;
        _model.TraceUpdated -= OnTraceUpdated; _model.SelectionChanged -= OnSelectionChanged; _model.PropertyChanged -= ModelChanged; _model.ComparisonUpdated -= OnComparisonUpdated;
        _ = _model.DisposeAsync();
    }
}
