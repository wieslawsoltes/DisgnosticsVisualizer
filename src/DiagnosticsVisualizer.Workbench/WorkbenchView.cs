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
public sealed class WorkbenchView : UserControl, IDisposable
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
    private readonly TextBox _filter = new() { Watermark = "Filter events, objects, operations…", Width = 250, FontSize = 11, Height = 30 };
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
    private readonly IRootDock _layout;
    private readonly Button _record, _connect;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly List<(string Name, string Shortcut, Func<Task> Run)> _commands = [];
    private bool _updating, _disposed;
    public WorkbenchModel Model => _model;
    public WorkbenchView() : this(new WorkbenchModel()) { }
    public WorkbenchView(WorkbenchModel model)
    {
        _model = model; MinWidth = 760; MinHeight = 440; Focusable = true;
        Background = new SolidColorBrush(Color.Parse("#0C111B"));
        _root = new Grid { RowDefinitions = new("52,76,Auto,*,27") };
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
            ["timeline"] = ChartPanel(_timeline, _timeline.Fit), ["flame"] = ChartPanel(_flame, _flame.Fit),
            ["causality"] = ChartPanel(_causal, _causal.Fit), ["systems"] = new ScrollViewer { Content = _systems },
            ["comparison"] = ComparisonPanel(), ["visual"] = TreePanel(_visual), ["logical"] = TreePanel(_logical),
            ["properties"] = _properties, ["details"] = _details, ["events"] = EventPanel(),
            ["findings"] = _findings, ["costs"] = CostPanel()
        };
        _factory = new(views); _layout = _factory.CreateLayout(); _factory.InitLayout(_layout); _dock.Layout = _layout;
        Grid.SetRow(_dock, 3); _root.Children.Add(_dock);
        var footer = BuildFooter(); Grid.SetRow(footer, 4); _root.Children.Add(footer);
        _overlay = new Border { Background = new SolidColorBrush(Color.Parse("#AA070B12")), IsVisible = false, ZIndex = 1000 };
        Grid.SetRowSpan(_overlay, 5); _root.Children.Add(_overlay); Content = _root;
        WireSelection(); RegisterCommands();
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
    private Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(16, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var logo = new Border { Width = 28, Height = 28, CornerRadius = new(7), Background = new SolidColorBrush(Color.Parse("#1C3D55")), Child =
            new TextBlock { Text = "D", FontWeight = FontWeight.Bold, FontSize = 18, Foreground = ChartPalette.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        brand.Children.Add(logo); brand.Children.Add(new TextBlock { Text = "Diagnostics Visualizer", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(1), CornerRadius = new(4), Padding = new(6, 3), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "AVALONIA", FontSize = 9, Foreground = ChartPalette.Muted } });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(ToolbarButton("Open", "M1 4H6L8 6H15V14H1ZM1 4V2H6L8 4H13V6", () => RunAsync(() => OpenAsync(false))));
        tools.Children.Add(ToolbarButton("Save", "M2 1H12L15 4V15H1V1ZM4 1V6H11V1M4 15V9H12V15", () => RunAsync(() => SaveAsync(false))));
        tools.Children.Add(new Border { Width = 1, Height = 22, Background = ChartPalette.Grid.Brush, Margin = new(5, 0) });
        tools.Children.Add(_record); tools.Children.Add(_connect);
        tools.Children.Add(ToolbarButton("Commands", "M2 4H14M2 8H14M2 12H14", () => { ShowCommands(); return Task.CompletedTask; }));
        grid.Children.Add(brand); Grid.SetColumn(tools, 2); grid.Children.Add(tools);
        return new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(0, 0, 0, 1), Child = grid };
    }
    private Control BuildMetrics()
    {
        var grid = new Grid { ColumnDefinitions = new("2.4*,*,*,*,*,*"), Margin = new(18, 10) };
        var sourcePanel = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        sourcePanel.Children.Add(new TextBlock { Text = "CAPTURE WORKSPACE", FontSize = 9, Foreground = ChartPalette.Accent }); sourcePanel.Children.Add(_source);
        grid.Children.Add(sourcePanel);
        var items = new[] { ("RETAINED EVENTS", _count), ("TRACE WINDOW", _duration), ("THREADS", _threadCount), ("TREE NODES", _nodeCount), ("LOST EVENTS", _loss) };
        for (var i = 0; i < items.Length; i++)
        {
            var stack = new StackPanel { Spacing = 3, Margin = new(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = items[i].Item1, FontSize = 9, Foreground = ChartPalette.Muted }); stack.Children.Add(items[i].Item2);
            var cell = new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(1, 0, 0, 0), Child = stack }; Grid.SetColumn(cell, i + 1); grid.Children.Add(cell);
        }
        return new Border { Background = new SolidColorBrush(Color.Parse("#111925")), BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(0, 0, 0, 1), Child = grid };
    }
    private Border BuildErrorBanner()
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(14, 8) }; grid.Children.Add(_error);
        var dismiss = new Button { Content = "Dismiss", FontSize = 10, Padding = new(8, 3), Background = Brushes.Transparent };
        dismiss.Click += (_, _) => _model.ClearError(); Grid.SetColumn(dismiss, 1); grid.Children.Add(dismiss);
        return new Border { IsVisible = false, Background = new SolidColorBrush(Color.Parse("#48252E")), Child = grid };
    }
    private Control BuildFooter()
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12, 4) }; grid.Children.Add(_status);
        var shortcuts = new TextBlock { Text = "500k rolling event cap  ·  Ctrl+Shift+P commands", FontSize = 10, Foreground = ChartPalette.Muted };
        Grid.SetColumn(shortcuts, 1); grid.Children.Add(shortcuts);
        return new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(0, 1, 0, 0), Child = grid };
    }
    private Control ChartPanel(Control chart, Action fit)
    {
        var grid = new Grid { RowDefinitions = new("35,*") };
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(10, 3) };
        toolbar.Children.Add(new TextBlock { Text = "Click to inspect · drag to pan · Ctrl+wheel to zoom", FontSize = 10, Foreground = ChartPalette.Muted, VerticalAlignment = VerticalAlignment.Center });
        var fitButton = new Button { Content = "Fit", Padding = new(12, 3), FontSize = 10, MinHeight = 25 }; fitButton.Click += (_, _) => fit();
        Grid.SetColumn(fitButton, 1); toolbar.Children.Add(fitButton); grid.Children.Add(toolbar);
        var scroll = new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = chart is TimelineControl ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll); return grid;
    }
    private Control TreePanel(TreeExplorer tree)
    {
        var grid = new Grid { RowDefinitions = new("31,*") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(8, 2), Spacing = 4 };
        var expand = new Button { Content = "Expand", Padding = new(8, 3), FontSize = 10 }; expand.Click += (_, _) => tree.ExpandAll();
        var collapse = new Button { Content = "Collapse", Padding = new(8, 3), FontSize = 10 }; collapse.Click += (_, _) => tree.CollapseAll();
        var refresh = new Button { Content = "Snapshot", Padding = new(8, 3), FontSize = 10 }; refresh.Click += async (_, _) => await RunAsync(_model.RequestSnapshotAsync);
        actions.Children.Add(expand); actions.Children.Add(collapse); actions.Children.Add(refresh); grid.Children.Add(actions); Grid.SetRow(tree, 1); grid.Children.Add(tree); return grid;
    }
    private Control EventPanel()
    {
        var grid = new Grid { RowDefinitions = new("40,*") };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(8, 4) };
        toolbar.Children.Add(_filter); toolbar.Children.Add(_category); toolbar.Children.Add(_objectOnly); grid.Children.Add(toolbar);
        Grid.SetRow(_events, 1); grid.Children.Add(_events); return grid;
    }
    private Control CostPanel()
    {
        var grid = new Grid { ColumnDefinitions = new("2*,3*") }; grid.Children.Add(_costChart); Grid.SetColumn(_costs, 1); grid.Children.Add(_costs); return grid;
    }
    private Control ComparisonPanel()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(14), Spacing = 12 };
        var load = new Button { Content = "Open baseline trace", FontSize = 11 }; load.Click += async (_, _) => await RunAsync(() => OpenAsync(true)); row.Children.Add(load);
        row.Children.Add(new TextBlock { Text = "Compare recorded self time. Use equivalent workloads and capture profiles.", FontSize = 11, Foreground = ChartPalette.Muted, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(row); Grid.SetRow(_comparison, 1); grid.Children.Add(_comparison); return grid;
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
        _costChart.CostSelected += cost => { _filter.Text = cost.Name; _factory.Activate("events"); };
        _costs.DoubleTapped += (_, _) => { if (_costs.SelectedItem is CostRow cost) { _filter.Text = cost.Name; _factory.Activate("events"); } };
    }
    private void OnTraceUpdated(TraceView trace, AnalysisResult analysis)
    {
        _updating = true;
        try
        {
            _timeline.Trace = trace; _flame.Trace = trace; _causal.SetTrace(trace, analysis, _model.SelectedEventId); _systems.SetTrace(trace, analysis);
            _visual.SetSnapshot(trace.Snapshot); _logical.SetSnapshot(trace.Snapshot); _costChart.Rows = analysis.Costs;
            _costs.ItemsSource = analysis.Costs; _findings.ItemsSource = analysis.Findings;
            _count.Text = trace.Events.Length.ToString("N0");
            _duration.Text = trace.Events.Length == 0 ? "0 s" : $"{trace.Milliseconds(trace.Events.Max(e => e.End) - trace.Events.Min(e => e.Timestamp)) / 1000:F2} s";
            _threadCount.Text = trace.Events.Select(e => e.ThreadId).Distinct().Count().ToString(); _nodeCount.Text = trace.Snapshot.Nodes.Length.ToString("N0");
            _loss.Text = trace.DroppedEvents.ToString("N0"); _loss.Foreground = trace.DroppedEvents > 0 ? new SolidColorBrush(Color.Parse("#F5B680")) : ChartPalette.Foreground;
            ToolTip.SetTip(_loss, $"{trace.MissingBlocks:N0} missing batches · {trace.ProbeFaults:N0} probe faults · {trace.MetadataDrops:N0} metadata drops · {trace.EvictedEvents:N0} events evicted from viewer history");
            RefreshEventRows();
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
        _properties.Inspect(trace, objectId); _details.Inspect(trace, selected);
        if (selected is { } e)
        {
            _causal.SelectEvent(e.Id);
            if (focus) { _timeline.FocusEvent(e); _flame.FocusEvent(e); _factory.Activate("timeline"); }
        }
        if (_objectOnly.IsChecked == true) RefreshEventRows();
    }
    private void OnComparisonUpdated() { _comparison.ItemsSource = _model.Comparison; _factory.Activate("comparison"); }
    private void RefreshEventRows()
    {
        var updating = _updating; _updating = true;
        try
        {
            var rows = _model.FilterEvents(_filter.Text, _category.SelectedIndex <= 0 ? null : (Subsystem)(_category.SelectedIndex - 1), _objectOnly.IsChecked == true);
            _events.ItemsSource = rows; _events.SelectedItem = rows.FirstOrDefault(row => row.Id == _model.SelectedEventId);
        }
        finally { _updating = updating; }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateChrome();
    private void UpdateChrome()
    {
        _source.Text = _model.Source; _status.Text = _model.Status;
        _error.Text = _model.Error; _errorBanner.IsVisible = !string.IsNullOrEmpty(_model.Error);
        _record.Content = _model.Recording && _model.Connected ? "Pause capture" : "Record"; _record.IsEnabled = _model.Connected;
        _connect.Content = _model.Connected ? "Disconnect" : "Connect";
    }
    private async Task OpenAsync(bool baseline)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File storage is unavailable in this host.");
        var files = await storage.OpenFilePickerAsync(new()
        {
            Title = baseline ? "Open baseline trace" : "Open Avalonia trace", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Avalonia binary trace") { Patterns = ["*.avtrace"], MimeTypes = ["application/octet-stream"] }]
        });
        if (files.Count == 0) return;
        var properties = await files[0].GetBasicPropertiesAsync();
        if (OperatingSystem.IsBrowser() && properties.Size > 512UL * 1024 * 1024)
            throw new InvalidOperationException("Browser imports are limited to 512 MiB. Use the desktop workbench for larger files.");
        await using var stream = await files[0].OpenReadAsync();
        if (baseline) await _model.CompareAsync(stream); else await _model.ImportAsync(stream, files[0].Name);
    }
    private async Task SaveAsync(bool chrome)
    {
        if (_model.Trace is null) throw new InvalidOperationException("Open or record a trace before exporting.");
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File storage is unavailable in this host.");
        var file = await storage.SaveFilePickerAsync(new()
        {
            Title = chrome ? "Export Chrome Trace JSON" : "Save retained trace", SuggestedFileName = $"capture-{_model.Trace.SessionId:N}.{(chrome ? "json" : "avtrace")}",
            DefaultExtension = chrome ? "json" : "avtrace", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(chrome ? "Chrome Trace JSON" : "Avalonia binary trace") { Patterns = [chrome ? "*.json" : "*.avtrace"] }]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync(); await _model.ExportAsync(stream, chrome);
    }
    private void ShowConnection()
    {
        var stack = new StackPanel { Spacing = 12, Margin = new(26) };
        stack.Children.Add(new TextBlock { Text = "Connect to an Avalonia application", FontSize = 19, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = "The target sends capture batches to the collector. Credentials stay in memory and are never added to the URL.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ChartPalette.Muted });
        var endpoint = new TextBox { Text = "ws://127.0.0.1:4317/trace", Watermark = "wss://collector.example/trace", FontSize = 12 };
        var token = new TextBox { Watermark = "Capture token (32+ characters)", PasswordChar = '●', FontSize = 12 };
        AutomationProperties.SetName(endpoint, "Collector endpoint"); AutomationProperties.SetName(token, "Capture authentication token");
        stack.Children.Add(new TextBlock { Text = "COLLECTOR ENDPOINT", FontSize = 10, Foreground = ChartPalette.Accent }); stack.Children.Add(endpoint);
        stack.Children.Add(new TextBlock { Text = "CAPTURE TOKEN", FontSize = 10, Foreground = ChartPalette.Accent }); stack.Children.Add(token);
        stack.Children.Add(new TextBlock { Text = "Remote connections require WSS/TLS with a trusted certificate. HTTPS-hosted browser viewers may also require WSS for a local collector.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = ChartPalette.Muted });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#FFC5C5")) }; stack.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => { token.Text = ""; CloseOverlay(); };
        var connect = new Button { Content = "Connect securely" }; connect.Classes.Add("dv-primary");
        connect.Click += async (_, _) =>
        {
            connect.IsEnabled = false;
            try { await _model.ConnectAsync(new Uri(endpoint.Text ?? ""), token.Text ?? ""); token.Text = ""; CloseOverlay(); }
            catch (Exception failure) { error.Text = $"{failure.GetType().Name}: {failure.Message}"; }
            finally { connect.IsEnabled = true; }
        };
        actions.Children.Add(cancel); actions.Children.Add(connect); stack.Children.Add(actions);
        ShowCard(stack, 560); Dispatcher.UIThread.Post(() => endpoint.Focus());
    }
    private void RegisterCommands()
    {
        _commands.Add(("Open trace", "Ctrl+O", () => OpenAsync(false)));
        _commands.Add(("Save retained trace", "Ctrl+S", () => SaveAsync(false)));
        _commands.Add(("Export Chrome Trace JSON", "", () => SaveAsync(true)));
        _commands.Add(("Compare with baseline trace", "", () => OpenAsync(true)));
        _commands.Add(("Connect to collector", "Ctrl+L", () => { ShowConnection(); return Task.CompletedTask; }));
        _commands.Add(("Disconnect collector", "", _model.DisconnectAsync));
        _commands.Add(("Pause / resume target recording", "F8", _model.ToggleRecordingAsync));
        _commands.Add(("Request fresh tree checkpoint", "", _model.RequestSnapshotAsync));
        _commands.Add(("Fit timeline", "F", () => { _timeline.Fit(); _flame.Fit(); return Task.CompletedTask; }));
        _commands.Add(("Open synthetic example", "", () => { _model.LoadDemo(); return Task.CompletedTask; }));
        foreach (var panel in new[] { ("timeline", "Timeline"), ("flame", "Flame chart"), ("causality", "Causal chain"), ("systems", "Subsystem map"),
            ("visual", "Visual tree"), ("logical", "Logical tree"), ("properties", "Property inspector"), ("details", "Event details"), ("events", "Event log"), ("findings", "Findings"), ("costs", "Hot paths") })
        {
            var id = panel.Item1; _commands.Add(($"Show {panel.Item2}", "", () => { _factory.Activate(id); return Task.CompletedTask; }));
        }
    }
    private void ShowCommands()
    {
        var search = new TextBox { Watermark = "Type a command…", Margin = new(18, 16), FontSize = 14 };
        var list = new ListBox { MaxHeight = 390, Margin = new(8, 0, 8, 12) };
        var filtered = _commands.ToArray();
        void Filter() { filtered = _commands.Where(command => command.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray(); list.ItemsSource = filtered.Select(command => command.Name + (command.Shortcut.Length == 0 ? "" : $"    {command.Shortcut}")).ToArray(); list.SelectedIndex = filtered.Length > 0 ? 0 : -1; }
        async Task Execute() { var index = list.SelectedIndex; if (index < 0 || index >= filtered.Length) return; var command = filtered[index]; CloseOverlay(); await RunAsync(command.Run); }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { await Execute(); e.Handled = true; }
            else if (e.Key == Key.Down) { list.SelectedIndex = Math.Min(filtered.Length - 1, list.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
        };
        list.DoubleTapped += async (_, _) => await Execute();
        var panel = new Grid { RowDefinitions = new("Auto,*") }; panel.Children.Add(search); Grid.SetRow(list, 1); panel.Children.Add(list); Filter(); ShowCard(panel, 560);
        Dispatcher.UIThread.Post(() => search.Focus());
    }
    private void ShowCard(Control content, double width)
    {
        _overlay.Child = new Border { Width = width, MaxWidth = Math.Max(300, Bounds.Width - 48), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.Parse("#182333")), BorderBrush = new SolidColorBrush(Color.Parse("#3A516D")), BorderThickness = new(1), CornerRadius = new(12), Child = content };
        _overlay.IsVisible = true;
    }
    private void CloseOverlay() { _overlay.IsVisible = false; _overlay.Child = null; Focus(); }
    private async void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _overlay.IsVisible) { CloseOverlay(); e.Handled = true; return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.P)
        { ShowCommands(); e.Handled = true; return; }
        if (_overlay.IsVisible) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.O) { e.Handled = true; await RunAsync(() => OpenAsync(false)); }
            else if (e.Key == Key.S) { e.Handled = true; await RunAsync(() => SaveAsync(false)); }
            else if (e.Key == Key.L) { e.Handled = true; ShowConnection(); }
        }
        else if (e.Key == Key.F8) { e.Handled = true; await RunAsync(_model.ToggleRecordingAsync); }
        else if (e.Key == Key.F && e.Source is not TextBox) { _timeline.Fit(); _flame.Fit(); e.Handled = true; }
    }
    private async Task RunAsync(Func<Task> action)
    { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { _model.ReportError($"{error.GetType().Name}: {error.Message}"); } }
    private static TextBlock MetricValue() => new() { Text = "0", FontSize = 21, FontWeight = FontWeight.SemiBold, Foreground = ChartPalette.Foreground };
    private static Button ToolbarButton(string label, string geometry, Func<Task> action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new global::Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(geometry), Width = 14, Height = 14, Stretch = Stretch.Uniform,
            Stroke = ChartPalette.Muted, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content }; button.Classes.Add("dv-toolbar"); AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
        button.Click += async (_, _) => await action(); return button;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _filterTimer.Stop();
        _model.TraceUpdated -= OnTraceUpdated; _model.SelectionChanged -= OnSelectionChanged; _model.PropertyChanged -= ModelChanged; _model.ComparisonUpdated -= OnComparisonUpdated;
        _ = _model.DisposeAsync();
    }
}
