using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed record DependencyRow(string Direction, string Kind, long ObjectId, string Object, string Detail);

public sealed class DependencyExplorer : UserControl
{
    private readonly DependencyGraphControl _graph = new();
    private readonly DataGrid _edges = DiagnosticGrid.Create(("Direction", "Direction", 85), ("Relationship", "Kind", 145),
        ("Object", "Object", -1), ("Id", "ObjectId", 85), ("Recorded detail", "Detail", -1));
    private readonly TextBlock _heading = new() { Margin = new(12, 8), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new(12, 4), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Watermark = "Filter relationships…", Width = 260, FontSize = 11 };
    private DependencyRow[] _rows = [];
    private ObjectDependencyGraph? _index;
    private SnapshotData? _snapshot;
    private IReadOnlyDictionary<long, ObjectInfo>? _metadata;
    public event Action<long>? ObjectSelected;
    public int RelationshipCount => _rows.Length;
    public long SelectedObjectId { get; private set; }
    public DependencyExplorer()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,2*,5,Auto,*") };
        root.Children.Add(_heading); Grid.SetRow(_status, 1); root.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _graph, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ChartPalette.Alternate };
        Grid.SetRow(splitter, 3); root.Children.Add(splitter);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(10, 5) }; toolbar.Children.Add(_search);
        var navigate = new Button { Content = "Inspect related object", FontSize = 11 }; navigate.Click += (_, _) => Navigate(); toolbar.Children.Add(navigate);
        Grid.SetRow(toolbar, 4); root.Children.Add(toolbar); Grid.SetRow(_edges, 5); root.Children.Add(_edges); Content = root;
        _graph.ObjectSelected += id => ObjectSelected?.Invoke(id); _edges.DoubleTapped += (_, _) => Navigate();
        _search.TextChanged += (_, _) => Filter();
        AutomationProperties.SetName(_edges, "Recorded object relationships"); AutomationProperties.SetName(navigate, "Inspect related object");
    }
    public void Inspect(TraceView trace, long selected, SnapshotData? snapshot = null)
    {
        snapshot ??= trace.Snapshot;
        if (!ReferenceEquals(snapshot, _snapshot) || !ReferenceEquals(trace.Objects, _metadata))
        { _snapshot = snapshot; _metadata = trace.Objects; _index = new(trace, snapshot); }
        SelectedObjectId = selected;
        var neighborhood = _index!.Neighborhood(selected); var node = _index.Node(selected);
        _heading.Text = $"{node.Label} · #{selected} · {node.Type}";
        _rows = _index.Edges(selected).Select(edge =>
        { var other = edge.Other(selected); return new DependencyRow(edge.Source == selected ? "Outgoing →" : "← Incoming", edge.Kind, other, _index.Node(other).Label, edge.Detail); }).ToArray();
        _status.Text = $"{_rows.Length:N0} recorded relationships · {neighborhood.OmittedEdges:N0} omitted from the bounded diagram (table retains all). " +
            (snapshot.Truncated ? "Incomplete checkpoint. " : "") + "Arrows describe references/ownership, not event causality or heap retention proof.";
        _graph.SetGraph(neighborhood); Filter();
    }
    private void Filter()
    {
        var text = _search.Text ?? "";
        _edges.ItemsSource = _rows.Where(r => r.Kind.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            r.Object.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void Navigate() { if (_edges.SelectedItem is DependencyRow row) ObjectSelected?.Invoke(row.ObjectId); }
}
