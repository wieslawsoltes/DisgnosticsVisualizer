using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed record DetailRow(string Field, string Value);
public sealed class EventDetailsControl : UserControl
{
    private readonly TextBlock _title = new() { Text = "Select an event", FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(14) };
    private readonly DataGrid _grid = DiagnosticGrid.Create(("Field", "Field", 130), ("Value", "Value", -1));
    private TraceEvent? _event;
    private readonly Button _parent = new() { Content = "Go to parent", FontSize = 11, IsEnabled = false };
    private readonly Button _object = new() { Content = "Inspect object", FontSize = 11, IsEnabled = false };
    private readonly Button _source = new() { Content = "Source", FontSize = 11, IsEnabled = false };
    public event Action? SourceRequested;
    public event Action<long>? ParentRequested;
    public event Action<long>? ObjectRequested;
    public EventDetailsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*") };
        var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new(12, 0, 12, 10) };
        _parent.Click += (_, _) => { if (_event is { ParentId: not 0 } e) ParentRequested?.Invoke(e.ParentId); };
        _object.Click += (_, _) => { if (_event is { ObjectId: not 0 } e) ObjectRequested?.Invoke(e.ObjectId); };
        _source.Click += (_, _) => SourceRequested?.Invoke();
        actions.Children.Add(_source); actions.Children.Add(_parent); actions.Children.Add(_object); Grid.SetRow(actions, 1); Grid.SetRow(_grid, 2);
        root.Children.Add(_title); root.Children.Add(actions); root.Children.Add(_grid); Content = root;
    }
    public void Inspect(TraceView trace, TraceEvent? selected)
    {
        _event = selected;
        _parent.IsEnabled = selected is { ParentId: not 0 };
        _object.IsEnabled = selected is { ObjectId: not 0 };
        _source.IsEnabled = selected is { } sourceEvent && trace.SourceSites.ContainsKey(sourceEvent.NameId);
        if (selected is not { } e) { _title.Text = "Select an event"; _grid.ItemsSource = Array.Empty<DetailRow>(); return; }
        _title.Text = trace.Name(e);
        var native = NativeMethodEvent.IsEvent(e);
        var rows = new List<DetailRow>
        {
            new DetailRow("Subsystem", e.Subsystem.ToString()), new("Kind", e.Kind.ToString()),
            new("Event ID", e.Id.ToString()), new("Causal parent", e.ParentId == 0 ? "Not recorded" : e.ParentId.ToString()),
            new("Object", e.ObjectId.ToString()), new("Related object", e.RelatedId.ToString()), new(native ? "Native thread slot" : "Managed thread", e.ThreadId.ToString()),
            new("Start", $"{trace.Milliseconds(e.Timestamp - trace.OriginTimestamp):F6} ms"), new("Inclusive duration", $"{trace.Milliseconds(e.Duration):F6} ms"),
            new("Clock frequency", $"{trace.Frequency:N0} Hz"), new("Data 0", e.Data0.ToString()), new("Data 1", e.Data1.ToString()),
            new("Interpretation", e.Kind == TraceEventKind.Span
                ? "Elapsed method span on the source monotonic clock; includes waits, not CPU utilization or GPU completion."
                : "Recorded marker on the source monotonic clock; not a measured execution duration."),
            new("Loss caveat", $"{trace.DroppedEvents:N0} events dropped; {trace.MissingBlocks:N0} batch gaps; {trace.EvictedEvents:N0} events outside retained history.")
        };
        if (native)
        {
            var thread = trace.NativeMetadata.GetValueOrDefault((2, e.ThreadId)) as NativeThreadInfo;
            rows.Insert(7, new("OS thread ID", thread?.OsThreadId.ToString() ?? "Not retained"));
            rows.Insert(8, new("Native outcome", NativeMethodEvent.Outcome(e)));
        }
        _grid.ItemsSource = rows;
    }
}
