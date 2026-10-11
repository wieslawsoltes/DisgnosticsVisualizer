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
    private readonly DataGrid _resources = DiagnosticGrid.Create(("Key", "Name", 140), ("Value", "Value", -1), ("Scope / dictionary", "Source", -1));
    private readonly DataGrid _relations = DiagnosticGrid.Create(("Relationship", "Kind", 120), ("Target", "Target", 65), ("Detail", "Detail", -1));
    private readonly TextBlock _precedence = new() { Margin = new(10, 6), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _selectedProperty = new() { Content = "Only selected property", IsChecked = true, Margin = new(10, 4), FontSize = 11 };
    private PropertySnapshot[] _filtered = [];
    private readonly LayoutBoxControl _box = new();
    private PropertySnapshot[] _all = [];
    private long _id;
    public event Action<long>? ObjectSelected;
    public PropertyInspector()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*") };
        var tabs = new TabControl { ItemsSource = new[]
        {
            new TabItem { Header = "Values", Content = _properties }, new TabItem { Header = "Precedence", Content = PrecedencePanel() },
            new TabItem { Header = "Resources", Content = ResourcePanel() }, new TabItem { Header = "Relations", Content = _relations },
            new TabItem { Header = "Layout", Content = new ScrollViewer { Content = _box, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } }
        }};
        _properties.SelectionChanged += (_, _) => UpdatePrecedence();
        _selectedProperty.IsCheckedChanged += (_, _) => UpdatePrecedence();
        _search.TextChanged += (_, _) => Filter();
        _relations.DoubleTapped += (_, _) => { if (_relations.SelectedItem is RelationSnapshot relation) ObjectSelected?.Invoke(relation.Source == _id ? relation.Target : relation.Source); };
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
        _properties.ItemsSource = filtered.Where(property => property.Kind is "Direct" or "Styled" or "Attached" or "Unavailable" or "Capability" or "Inspection limit").ToArray();
        _filtered = filtered; UpdatePrecedence();
        _resources.ItemsSource = filtered.Where(property => property.Kind.Contains("resource", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private Control PrecedencePanel()
    {
        var panel = new Grid { RowDefinitions = new("Auto,Auto,*") };
        panel.Children.Add(_selectedProperty); Grid.SetRow(_precedence, 1); panel.Children.Add(_precedence);
        Grid.SetRow(_frames, 2); panel.Children.Add(_frames); return panel;
    }
    private Control ResourcePanel()
    {
        var panel = new Grid { RowDefinitions = new("Auto,*") };
        panel.Children.Add(new TextBlock { Margin = new(10, 6), FontSize = 10, Foreground = ChartPalette.Muted,
            TextWrapping = TextWrapping.Wrap, Text = "Local, ancestor, merged and theme candidates. Inventory order is not lookup precedence. Deferred resources are not evaluated; text remains redacted." });
        Grid.SetRow(_resources, 1); panel.Children.Add(_resources); return panel;
    }
    private void UpdatePrecedence()
    {
        var selected = _properties.SelectedItem as PropertySnapshot;
        _frames.ItemsSource = _filtered.Where(p => p.Kind.Contains("frame", StringComparison.OrdinalIgnoreCase) &&
            (_selectedProperty.IsChecked != true || selected is null || p.Name == selected.Name)).ToArray();
        _precedence.Text = selected is null ? "Select a property in Values to inspect its applied frames. Active does not imply winning." :
            $"{selected.Name}: effective {selected.Value} · {selected.Priority} · {selected.Source}. Active frames are candidates, not proof of the winning setter.";
    }
}
