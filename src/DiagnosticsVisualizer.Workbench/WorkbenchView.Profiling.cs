using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Workbench.Docking;
using DiagnosticsVisualizer.Workbench.Persistence;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    private readonly ActivityOverview _overview = new() { Height = 76 };
    private readonly ComboBox _presetPicker = new() { ItemsSource = Enum.GetValues<WorkspacePreset>(), SelectedIndex = 0, Width = 100, FontSize = 11, MinHeight = 28 };
    private readonly LayoutSceneControl _scene = new();
    private readonly DataGrid _statistics = DiagnosticGrid.Create(("Operation", "Name", -1), ("Calls", "Count", 65), ("Self ms", "SelfMs", 90),
        ("Total ms", "TotalMs", 90), ("Mean ms", "AverageMs", 90), ("p50 ms", "P50Ms", 90), ("p95 ms", "P95Ms", 90), ("p99 ms", "P99Ms", 90));
    private readonly DataGrid _topDown = DiagnosticGrid.Create(("Calling context", "Operation", -1), ("Calls", "Count", 65), ("Inclusive ms", "TotalMs", 100), ("Self ms", "SelfMs", 95));
    private readonly DataGrid _bottomUp = DiagnosticGrid.Create(("Operation ← callers", "Operation", -1), ("Samples", "Count", 70), ("Attributed ms", "TotalMs", 110), ("Own ms", "SelfMs", 90));
    private readonly DataGrid _threads = DiagnosticGrid.Create(("Thread", "ThreadId", 70), ("Events", "Events", 80), ("Busy ms", "BusyMs", 100), ("Span coverage %", "OccupancyPercent", -1));
    private readonly DataGrid _bookmarks = DiagnosticGrid.Create(("Event", "EventId", 70), ("Object", "ObjectId", 70), ("Bookmark", "Label", -1));
    private readonly DataGrid _changes = DiagnosticGrid.Create(("Object", "ObjectId", 75), ("Change", "Kind", 140), ("Before", "Before", -1), ("After", "After", -1));
    private readonly ComboBox _history = new() { MinWidth = 220, FontSize = 11 };
    private readonly TextBlock _rangeLabel = new() { Text = "Entire recording", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _queryError = new() { FontSize = 10, Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _captureHealth = new() { FontSize = 12, LineHeight = 24, TextWrapping = TextWrapping.Wrap, Margin = new(16) };
    private readonly DispatcherTimer _workspaceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private WorkspacePreset _preset = WorkspacePreset.Inspect;
    private string? _savedWorkspace;
    private SnapshotData? _historicalSnapshot;
    private bool _historyUpdating, _workspaceRestoring;
    public string WorkspacePresetName => _preset.ToString();
    public string ThemeName => "Rider Dark";
    public string? WorkspaceError { get; private set; }
    public string ExportWorkspace() => WorkspaceLayout.Serialize(_layout, _preset);
    public void ImportWorkspace(string json)
    {
        var state = WorkspaceLayout.Deserialize(json, _factory.Panels.Keys.ToHashSet(StringComparer.Ordinal));
        _workspaceRestoring = true;
        try
        {
            foreach (var window in _layout.Windows?.ToArray() ?? []) window.Exit();
            _dock.Layout = null;
            _layout = WorkspaceLayout.Restore(state, _factory); _preset = state.Preset;
            _factory.InitLayout(_layout); _dock.Layout = _layout; _presetPicker.SelectedItem = _preset; WorkspaceError = null;
        }
        finally { _workspaceRestoring = false; }
    }
    public void ApplyWorkspacePreset(WorkspacePreset preset)
    {
        foreach (var window in _layout.Windows?.ToArray() ?? []) window.Exit();
        _dock.Layout = null; _preset = preset; _layout = _factory.CreateLayout(preset); _factory.InitLayout(_layout); _dock.Layout = _layout; _presetPicker.SelectedItem = _preset;
        SaveWorkspace();
    }
    public void SaveWorkspace()
    {
        if (_workspaceRestoring) return;
        try
        {
            foreach (var window in _layout.Windows ?? []) window.Save();
            var json = ExportWorkspace(); if (json == _savedWorkspace) return;
            WorkspaceServices.Storage.Write(json); _savedWorkspace = json; WorkspaceError = null;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        { WorkspaceError = $"Workspace persistence: {error.Message}"; }
    }
    private void InitializeProfiling()
    {
        _statistics.Name = "StatisticsGrid"; _events.Name = "EventsGrid";
        _presetPicker.SelectionChanged += (_, _) => { if (!_workspaceRestoring && _presetPicker.SelectedItem is WorkspacePreset preset && preset != _preset) ApplyWorkspacePreset(preset); };
        _overview.RangeSelected += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _timeline.ViewportChanged += (start, end) => _flame.SetViewport(start, end);
        _flame.ViewportChanged += (start, end) => _timeline.SetViewport(start, end);
        _scene.ObjectSelected += id => _model.SelectObject(id, false);
        _statistics.SelectionChanged += (_, _) => { if (_statistics.SelectedItem is ProfileRow row) _model.SelectEvent(row.EventId, false); };
        foreach (var grid in new[] { _topDown, _bottomUp })
            grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is CallTreeRow row) _model.SelectEvent(row.EventId, false); };
        _bookmarks.DoubleTapped += (_, _) => { if (_bookmarks.SelectedItem is TraceBookmark bookmark) _model.SelectEvent(bookmark.EventId, true); };
        _model.ProfileUpdated += ProfileChanged; _model.BookmarksUpdated += BookmarksChanged;
        _workspaceTimer.Tick += (_, _) => SaveWorkspace();
        AttachedToVisualTree += (_, _) => _workspaceTimer.Start();
        DetachedFromVisualTree += (_, _) => { _workspaceTimer.Stop(); SaveWorkspace(); };
        try { if (WorkspaceServices.Storage.Read() is { Length: > 0 } json) { ImportWorkspace(json); _savedWorkspace = json; } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        { WorkspaceError = $"Saved workspace rejected: {error.Message}"; }
        ProfileChanged(); BookmarksChanged();
    }
    private void ProfileChanged()
    {
        if (_model.FullProfile is { } full) _overview.SetProfile(full, _model.Range);
        if (_model.Profile is not { } profile) return;
        _statistics.ItemsSource = profile.Operations; _topDown.ItemsSource = profile.TopDown; _bottomUp.ItemsSource = profile.BottomUp; _threads.ItemsSource = profile.Threads;
        _rangeLabel.Text = _model.Range is null ? $"Entire recording · {profile.Events:N0} events" :
            $"Selected range · {_model.Trace!.Milliseconds(profile.Range.Duration):N3} ms · {profile.Events:N0} events";
        if (_model.Range is { } range) { _timeline.SetViewport(range.Start, range.End); _flame.SetViewport(range.Start, range.End); }
        if (profile.Truncated) _rangeLabel.Text += " · call-tree depth/node limit reached";
        RefreshEventRows();
    }
    private void BookmarksChanged() => _bookmarks.ItemsSource = _model.Bookmarks.ToArray();
    private Control BuildDockArea()
    {
        var area = new Grid { ColumnDefinitions = new("36,*,36") };
        var left = new StackPanel { Spacing = 5, Margin = new(2, 6) };
        var right = new StackPanel { Spacing = 5, Margin = new(2, 6) };
        foreach (var (id, title, path) in new[] {
            ("visual", "Visual tree", "M2 2H6V6H2ZM10 10H14V14H10ZM4 6V12H10M10 2H14V6H10Z"),
            ("logical", "Logical tree", "M2 2H6V6H2ZM10 6H14V10H10ZM2 10H6V14H2ZM6 4H8V8H10M4 6V10"),
            ("scene", "Layout scene", "M1 1H15V15H1ZM4 4H9V8H4ZM7 10H12V13H7Z"),
            ("history", "Snapshot history", "M2 7A6 6 0 1 1 3 12M2 2V7H7M8 4V8L11 10"),
            ("health", "Capture health", "M1 8H4L6 3L9 13L11 8H15") }) left.Children.Add(PanelButton(id, title, path));
        foreach (var (id, title, path) in new[] {
            ("properties", "Properties and resources", "M2 3H14M2 8H14M2 13H14M5 1V5M11 6V10M6 11V15"),
            ("sources", "Sources (F12)", "M5 2L1 8L5 14M11 2L15 8L11 14M9 1L7 15"),
            ("details", "Event details", "M8 2A6 6 0 1 0 8 14A6 6 0 1 0 8 2ZM8 7V11M8 4V5"),
            ("bookmarks", "Bookmarks", "M4 1H12V15L8 11L4 15Z"),
            ("threads", "Thread activity", "M2 14V8H5V14M7 14V2H10V14M12 14V5H15V14") }) right.Children.Add(PanelButton(id, title, path));
        area.Children.Add(new Border { Background = ChartPalette.Alternate, Child = left });
        Grid.SetColumn(_dock, 1); area.Children.Add(_dock);
        var side = new Border { Background = ChartPalette.Alternate, Child = right }; Grid.SetColumn(side, 2); area.Children.Add(side); return area;
    }
    private Button PanelButton(string id, string title, string path)
    {
        var button = new Button { Width = 30, Height = 30, Padding = new(6), Background = Brushes.Transparent, BorderThickness = new(0),
            Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(path), Stroke = ChartPalette.Muted, StrokeThickness = 1.2, Stretch = Stretch.Uniform } };
        Avalonia.Automation.AutomationProperties.SetName(button, title); ToolTip.SetTip(button, title);
        button.Click += (_, _) => _factory.Activate(id); return button;
    }
    private Control RangeBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(12, 3) };
        row.Children.Add(_rangeLabel);
        var clear = new Button { Content = "Clear range", FontSize = 10, Padding = new(8, 3) };
        clear.Click += async (_, _) => { await RunAsync(() => _model.SetRangeAsync(null)); _timeline.Fit(); _flame.Fit(); }; row.Children.Add(clear);
        var mark = new Button { Content = "Bookmark selection", FontSize = 10, Padding = new(8, 3) }; mark.Click += (_, _) => _model.AddBookmark(); row.Children.Add(mark);
        return row;
    }
    private Control HistoryPanel()
    {
        var panel = new Grid { RowDefinitions = new("Auto,Auto,*") };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(12, 8) };
        header.Children.Add(_history); header.Children.Add(new TextBlock { Text = "Changes from the preceding retained checkpoint", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);
        var note = new TextBlock { Margin = new(12, 3), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap,
            Text = "Checkpoints are sampled, weakly consistent observations—not exact event-time states. Truncated absence is unknown. Inspection follows the selected checkpoint." };
        Grid.SetRow(note, 1); panel.Children.Add(note); Grid.SetRow(_changes, 2); panel.Children.Add(_changes);
        _history.SelectionChanged += (_, _) => { if (!_historyUpdating) SelectCheckpoint(); };
        _changes.SelectionChanged += (_, _) => { if (_changes.SelectedItem is SnapshotChangeRow row) _model.SelectObject(row.ObjectId, false); };
        return panel;
    }
    private sealed record SnapshotChangeRow(long ObjectId, string Kind, string Before, string After);
    private void SelectCheckpoint()
    {
        if (_model.Trace is not { } trace) return;
        var index = _history.SelectedIndex - 1; _historicalSnapshot = index < 0 || index >= trace.Checkpoints.Length ? null : trace.Checkpoints[index].Data;
        var snapshot = _historicalSnapshot ?? trace.Snapshot;
        _visual.SetSnapshot(snapshot); _logical.SetSnapshot(snapshot); _scene.SetSnapshot(snapshot, _model.SelectedObjectId);
        _properties.Inspect(trace with { Snapshot = snapshot }, _model.SelectedObjectId);
        if (index <= 0) { _changes.ItemsSource = Array.Empty<SnapshotChangeRow>(); return; }
        var difference = SnapshotDiffer.Compare(trace.SessionId, trace.Checkpoints[index - 1].Data, trace.SessionId, snapshot);
        _changes.ItemsSource = difference.Nodes.Select(n => new SnapshotChangeRow(n.ObjectId, n.Kind.ToString(), NodeLabel(n.Before), NodeLabel(n.After)))
            .Concat(difference.Properties.Select(p => new SnapshotChangeRow(p.ObjectId, $"Property: {p.Name}", p.Before?.Value ?? "—", p.After?.Value ?? "—"))).ToArray();
        static string NodeLabel(NodeSnapshot? node) => node is null ? "—" : $"{node.Type} {node.Name} [{node.X:N1}, {node.Y:N1}, {node.Width:N1} × {node.Height:N1}]";
    }
    private void UpdateProfilingTrace(TraceView trace)
    {
        _historyUpdating = true;
        try
        {
            var selection = _history.SelectedIndex;
            _history.ItemsSource = new[] { "Live / latest checkpoint" }.Concat(trace.Checkpoints.Select((c, i) => $"Checkpoint {i + 1} · batch {c.Sequence}" )).ToArray();
            _history.SelectedIndex = Math.Clamp(selection, 0, trace.Checkpoints.Length);
        }
        finally { _historyUpdating = false; }
        SelectCheckpoint();
        _captureHealth.Text = $"RECORDING INTEGRITY\n\n{trace.Events.Length:N0} retained events\n{trace.DroppedEvents:N0} producer drops\n{trace.MissingBlocks:N0} missing batches\n{trace.ProbeFaults:N0} probe faults\n{trace.MetadataDrops:N0} metadata drops\n{trace.EvictedEvents:N0} viewer evictions\n{trace.Checkpoints.Length:N0} retained checkpoints\n\n" +
            "Managed spans measure elapsed time and can include waits; they are not CPU utilization or GPU completion. Missing spans make self-time estimates incomplete.\n\n" +
            (_model.IsDemo ? "SYNTHETIC DEMONSTRATION\nNo application is connected." : $"Session: {trace.SessionId}\nClock: {trace.Frequency:N0} ticks/second");
    }
    private void RegisterProfilingCommands()
    {
        _commands.Add(("Bookmark selected event", "Ctrl+B", () => { _model.AddBookmark(); return Task.CompletedTask; }));
        _commands.Add(("Clear analysis range", "", () => _model.SetRangeAsync(null)));
        _commands.Add(("Save workspace layout", "", () => { SaveWorkspace(); return Task.CompletedTask; }));
        foreach (var preset in Enum.GetValues<WorkspacePreset>())
            _commands.Add(($"Workspace: {preset}", "", () => { ApplyWorkspacePreset(preset); return Task.CompletedTask; }));
        foreach (var (id, title) in new[] { ("topdown", "Call tree"), ("bottomup", "Bottom-up"), ("statistics", "Statistics"), ("threads", "Thread activity"),
            ("scene", "Layout scene"), ("history", "Snapshot history"), ("bookmarks", "Bookmarks"), ("health", "Capture health") })
            _commands.Add(($"Show {title}", "", () => { _factory.Activate(id); return Task.CompletedTask; }));
    }
}
