using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;

namespace DiagnosticsVisualizer.Controls;

/// <summary>Observed compiler-PDB origins, not a reconstructed or executable markup document.</summary>
public sealed class XamlOriginsControl : UserControl
{
    private readonly DataGrid _grid = DiagnosticGrid.Create(("Object", "ObjectId", 100), ("Runtime type", "Type", -1),
        ("XAML location", "Location", 220), ("Observation", "Kind", 130), ("Original IL", "Instruction", 125));
    private readonly TextBox _search = new() { Watermark = "Find XAML file, type or location…", MaxLength = 512, FontSize = 11, MinWidth = 200 };
    private readonly CheckBox _selectedOnly = new() { Content = "Selected object", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(12, 6) };
    private XamlSourceIndex? _index;
    private Dictionary<long, XamlObjectOrigin> _visibleObjects = [];
    private long _objectId;
    private bool _updating;
    public event Action<long>? SourceRequested;
    public event Action<long>? ObjectSelected;
    public int OriginCount => _index?.Origins.Length ?? 0;
    public XamlOriginsControl()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*") };
        root.Children.Add(new TextBlock { Margin = new(12, 8), FontSize = 10, Foreground = ChartPalette.Muted, TextWrapping = TextWrapping.Wrap,
            Text = "Observed construction and root population at original compiler sequence points. Repeated template instances keep separate object identities. No origin does not mean 'not XAML'." });
        Grid.SetRow(_summary, 1); root.Children.Add(_summary);
        var tools = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Margin = new(10, 6) };
        tools.Children.Add(_search); Grid.SetColumn(_selectedOnly, 1); tools.Children.Add(_selectedOnly);
        var source = new Button { Content = "Reveal source", FontSize = 11 };
        source.Click += (_, _) => Reveal(); Grid.SetColumn(source, 2); tools.Children.Add(source);
        Grid.SetRow(tools, 2); root.Children.Add(tools); Grid.SetRow(_grid, 3); root.Children.Add(_grid); Content = root;
        AutomationProperties.SetName(_grid, "XAML object origins"); AutomationProperties.SetName(_search, "Find XAML origin");
        _search.TextChanged += (_, _) => Refresh();
        _selectedOnly.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) Refresh(); };
        _grid.SelectionChanged += (_, _) => { if (!_updating && _grid.SelectedItem is XamlObjectOrigin row) { _objectId = row.ObjectId; ObjectSelected?.Invoke(row.ObjectId); } };
        _grid.DoubleTapped += (_, _) => Reveal();
    }
    private void Reveal() { if (_grid.SelectedItem is XamlObjectOrigin row) SourceRequested?.Invoke(row.EventId); }
    public void Inspect(XamlSourceIndex index, long objectId)
    {
        if (ReferenceEquals(index, _index) && objectId == _objectId) return;
        var changed = !ReferenceEquals(index, _index); _index = index; _objectId = objectId;
        if (!changed && _selectedOnly.IsChecked != true) { SelectCurrentObject(); return; }
        if (changed) _summary.Text = index.Origins.Length == 0 ? "No XAML object origins retained. Keep portable PDBs during weaving and enable AVTRACE_XAML_OBJECTS=1." :
            $"{index.Origins.Length:N0} observations · {index.Origins.Select(r => r.ObjectId).Distinct().Count():N0} objects · {index.Origins.Select(r => r.Site.Document).Distinct().Count():N0} documents · source text stays local";
        Refresh();
    }
    private void Refresh()
    {
        var text = _search.Text ?? "";
        var rows = (_index?.Origins ?? []).Where(r => (_selectedOnly.IsChecked != true || r.ObjectId == _objectId) &&
            (r.Type.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Location.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        _visibleObjects = new Dictionary<long, XamlObjectOrigin>();
        foreach (var row in rows) _visibleObjects[row.ObjectId] = row;
        _updating = true;
        try { _grid.ItemsSource = rows; _grid.SelectedItem = _visibleObjects.GetValueOrDefault(_objectId); }
        finally { _updating = false; }
    }
    private void SelectCurrentObject()
    {
        _updating = true;
        try { _grid.SelectedItem = _visibleObjects.GetValueOrDefault(_objectId); }
        finally { _updating = false; }
    }
}
