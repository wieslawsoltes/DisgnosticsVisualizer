using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed class PropertyInspector : UserControl
{
    private readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Text = "No selection", Margin = new(12, 10, 12, 4) };
    private readonly TextBlock _identity = new() { FontSize = 10, Foreground = ChartPalette.Muted, Margin = new(12, 0, 12, 10), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Watermark = "Filter properties…", Margin = new(10, 4, 10, 8), FontSize = 11, Height = 30 };
    private readonly DataGrid _properties = DiagnosticGrid.Create(("Property", "Name", 130), ("Value", "Value", -1), ("Priority", "Priority", 100));
    private readonly DataGrid _frames = DiagnosticGrid.Create(("Property", "Name", 110), ("Frame", "Kind", 130), ("Priority", "Priority", 100), ("Source / selector", "Source", -1));
    private readonly DataGrid _resources = DiagnosticGrid.Create(("Key", "Name", 140), ("Value", "Value", -1), ("Source", "Source", 140));
    private readonly DataGrid _relations = DiagnosticGrid.Create(("Relationship", "Kind", 120), ("Target", "Target", 65), ("Detail", "Detail", -1));
    private readonly LayoutBoxControl _box = new();
    private PropertySnapshot[] _all = [];
    private long _id;
    public event Action<long>? ObjectSelected;
    public PropertyInspector()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*") };
        var tabs = new TabControl { ItemsSource = new[]
        {
            new TabItem { Header = "Values", Content = _properties }, new TabItem { Header = "Precedence", Content = _frames },
            new TabItem { Header = "Resources", Content = _resources }, new TabItem { Header = "Relations", Content = _relations },
            new TabItem { Header = "Layout", Content = new ScrollViewer { Content = _box, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } }
        }};
        _search.TextChanged += (_, _) => Filter();
        _relations.DoubleTapped += (_, _) => { if (_relations.SelectedItem is RelationSnapshot relation) ObjectSelected?.Invoke(relation.Target); };
        Grid.SetRow(_identity, 1); Grid.SetRow(_search, 2); Grid.SetRow(tabs, 3);
        root.Children.Add(_heading); root.Children.Add(_identity); root.Children.Add(_search); root.Children.Add(tabs); Content = root;
    }
    public void Inspect(TraceView trace, long objectId)
    {
        _id = objectId; var node = trace.Snapshot.Nodes.FirstOrDefault(node => node.Id == objectId);
        trace.Objects.TryGetValue(objectId, out var info);
        _heading.Text = node is null ? info?.Display ?? $"Object #{objectId}" : node.Type[(node.Type.LastIndexOf('.') + 1)..] + (node.Name.Length == 0 ? "" : $" · {node.Name}");
        _identity.Text = $"#{objectId}  {node?.Type ?? info?.Type ?? "No object metadata in this capture"}";
        _box.Node = node; _all = trace.Snapshot.Properties.Where(property => property.ObjectId == objectId).ToArray();
        _relations.ItemsSource = trace.Snapshot.Relations.Where(relation => relation.Source == objectId || relation.Target == objectId).ToArray(); Filter();
    }
    private void Filter()
    {
        var text = _search.Text?.Trim() ?? "";
        var filtered = _all.Where(property => text.Length == 0 || property.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || property.Source.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        _properties.ItemsSource = filtered.Where(property => property.Kind is "Direct" or "Styled" or "Attached" or "Unavailable" or "Capability").ToArray();
        _frames.ItemsSource = filtered.Where(property => property.Kind.Contains("frame", StringComparison.OrdinalIgnoreCase)).ToArray();
        _resources.ItemsSource = filtered.Where(property => property.Kind.Contains("resource", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
