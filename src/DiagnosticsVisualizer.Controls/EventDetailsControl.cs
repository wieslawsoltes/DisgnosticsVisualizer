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
    public event Action<long>? ParentRequested;
    public event Action<long>? ObjectRequested;
    public EventDetailsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*") };
        var actions = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new(12, 0, 12, 10) };
        var parent = new Button { Content = "Go to parent", FontSize = 11 }; parent.Click += (_, _) => { if (_event is { ParentId: not 0 } e) ParentRequested?.Invoke(e.ParentId); };
        var obj = new Button { Content = "Inspect object", FontSize = 11 }; obj.Click += (_, _) => { if (_event is { ObjectId: not 0 } e) ObjectRequested?.Invoke(e.ObjectId); };
        actions.Children.Add(parent); actions.Children.Add(obj); Grid.SetRow(actions, 1); Grid.SetRow(_grid, 2);
        root.Children.Add(_title); root.Children.Add(actions); root.Children.Add(_grid); Content = root;
    }
    public void Inspect(TraceView trace, TraceEvent? selected)
    {
        _event = selected;
        if (selected is not { } e) { _title.Text = "Select an event"; _grid.ItemsSource = Array.Empty<DetailRow>(); return; }
        _title.Text = trace.Name(e);
        _grid.ItemsSource = new[]
        {
            new DetailRow("Subsystem", e.Subsystem.ToString()), new("Kind", e.Kind.ToString()),
            new("Event ID", e.Id.ToString()), new("Synchronous parent", e.ParentId == 0 ? "Not recorded" : e.ParentId.ToString()),
            new("Object", e.ObjectId.ToString()), new("Related object", e.RelatedId.ToString()), new("Managed thread", e.ThreadId.ToString()),
            new("Start", $"{trace.Milliseconds(e.Timestamp - trace.OriginTimestamp):F6} ms"), new("Inclusive duration", $"{trace.Milliseconds(e.Duration):F6} ms"),
            new("Clock frequency", $"{trace.Frequency:N0} Hz"), new("Data 0", e.Data0.ToString()), new("Data 1", e.Data1.ToString()),
            new("Interpretation", "CPU wall duration on the source monotonic clock. A span is not a GPU timestamp."),
            new("Loss caveat", $"{trace.DroppedEvents:N0} events dropped; {trace.MissingBlocks:N0} batch gaps; {trace.EvictedEvents:N0} events outside retained history.")
        };
    }
}
