using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed record TreeRow(NodeSnapshot Node, int Depth, bool HasChildren, bool Expanded)
{
    public long Id => Node.Id;
    public string Label => Node.Type[(Node.Type.LastIndexOf('.') + 1)..] + (string.IsNullOrEmpty(Node.Name) ? "" : $"  #{Node.Name}");
}

/// <summary>A flattened, virtualized visual or logical tree. Expansion never materializes one control per node.</summary>
public sealed class TreeExplorer : UserControl
{
    private readonly TextBox _search = new() { Watermark = "Filter controls, names, classes…", Margin = new(10, 8), Height = 32, FontSize = 11 };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new(0), SelectionMode = SelectionMode.Single };
    private readonly TextBlock _status = new() { Margin = new(12, 7), FontSize = 10, Foreground = ChartPalette.Muted };
    private readonly HashSet<long> _expanded = [];
    private Dictionary<long, NodeSnapshot> _nodes = [];
    private Dictionary<long, NodeSnapshot[]> _children = [];
    private bool _updating;
    private long _selected;
    public bool Logical { get; }
    public event Action<long>? ObjectSelected;
    public TreeExplorer(bool logical = false)
    {
        Logical = logical;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        _list.ItemTemplate = new FuncDataTemplate<TreeRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var layout = new Grid { ColumnDefinitions = new("18,*,Auto"), Margin = new(Math.Min(row.Depth, 40) * 13 + 4, 0, 7, 0), Height = 27 };
            var expander = new Button
            {
                Content = row.HasChildren ? row.Expanded ? "▾" : "▸" : "·", Width = 18, Height = 24, Padding = new(0),
                Background = Brushes.Transparent, BorderThickness = new(0), Focusable = false, FontSize = 12
            };
            expander.Click += (_, e) => { if (row.HasChildren) { if (!_expanded.Add(row.Id)) _expanded.Remove(row.Id); Rebuild(); } e.Handled = true; };
            var label = new TextBlock { Text = row.Label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = row.Node.Visible ? 1 : .45 };
            var id = new TextBlock { Text = row.Id.ToString(), FontSize = 9, Foreground = ChartPalette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
            Grid.SetColumn(label, 1); Grid.SetColumn(id, 2); layout.Children.Add(expander); layout.Children.Add(label); layout.Children.Add(id);
            ToolTip.SetTip(layout, $"{row.Node.Type}\nVisual parent #{row.Node.VisualParent} · logical parent #{row.Node.LogicalParent}\nTemplated parent #{row.Node.TemplatedParent}\nClasses: {row.Node.Classes}");
            return layout;
        });
        _list.SelectionChanged += (_, _) =>
        {
            if (_updating || _list.SelectedItem is not TreeRow row) return;
            _selected = row.Id; ObjectSelected?.Invoke(row.Id);
        };
        _search.TextChanged += (_, _) => Rebuild();
        Grid.SetRow(_list, 1); Grid.SetRow(_status, 2); root.Children.Add(_search); root.Children.Add(_list); root.Children.Add(_status); Content = root;
    }
    public void SetSnapshot(SnapshotData snapshot)
    {
        _nodes = snapshot.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.Last());
        _children = _nodes.Values.GroupBy(node => Parent(node)).ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id).ToArray());
        if (_expanded.Count == 0)
        {
            foreach (var node in _nodes.Values.Where(node => Parent(node) == 0 || !_nodes.ContainsKey(Parent(node))))
            { _expanded.Add(node.Id); foreach (var child in _children.GetValueOrDefault(node.Id) ?? []) _expanded.Add(child.Id); }
        }
        _expanded.IntersectWith(_nodes.Keys); Rebuild();
        _status.Text = $"{_nodes.Count:N0} nodes · {(Logical ? "logical ownership" : "visual composition")}{(snapshot.Truncated ? " · truncated" : "")}";
    }
    public void SelectObject(long id, bool reveal = false)
    {
        _selected = id;
        if (reveal && _nodes.TryGetValue(id, out var node))
        {
            var seen = new HashSet<long>();
            while (seen.Add(node.Id) && _nodes.TryGetValue(Parent(node), out var parent)) { _expanded.Add(parent.Id); node = parent; }
            Rebuild();
        }
        _updating = true;
        try
        {
            var row = (_list.ItemsSource as IEnumerable<TreeRow>)?.FirstOrDefault(row => row.Id == id); _list.SelectedItem = row;
            if (reveal && row is not null) _list.ScrollIntoView(row);
        }
        finally { _updating = false; }
    }
    public void ExpandAll() { foreach (var id in _nodes.Keys) _expanded.Add(id); Rebuild(); }
    public void CollapseAll() { _expanded.Clear(); Rebuild(); }
    private long Parent(NodeSnapshot node) => Logical ? node.LogicalParent : node.VisualParent;
    private void Rebuild()
    {
        var filter = _search.Text?.Trim() ?? ""; var rows = new List<TreeRow>(); var seen = new HashSet<long>();
        if (filter.Length > 0)
        {
            foreach (var node in _nodes.Values.OrderBy(node => node.Id))
                if (node.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    node.Classes.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Id.ToString() == filter)
                    rows.Add(new(node, 0, _children.ContainsKey(node.Id), _expanded.Contains(node.Id)));
        }
        else
        {
            var pending = new Stack<(NodeSnapshot Node, int Depth)>();
            foreach (var node in _nodes.Values.Where(node => Parent(node) == 0 || !_nodes.ContainsKey(Parent(node))).OrderByDescending(node => node.Id)) pending.Push((node, 0));
            while (pending.Count > 0)
            {
                var (node, depth) = pending.Pop(); if (!seen.Add(node.Id)) continue;
                var children = _children.GetValueOrDefault(node.Id) ?? [];
                rows.Add(new(node, depth, children.Length > 0, _expanded.Contains(node.Id)));
                if (!_expanded.Contains(node.Id) || depth > 256) continue;
                for (var i = children.Length - 1; i >= 0; i--) pending.Push((children[i], depth + 1));
            }
            // Corrupt/disconnected cycles are not silently hidden from the investigator.
            foreach (var node in _nodes.Values.Where(node => !seen.Contains(node.Id) && Parent(node) == node.Id)) rows.Add(new(node, 0, false, false));
        }
        _updating = true;
        try { _list.ItemsSource = rows; _list.SelectedItem = rows.FirstOrDefault(row => row.Id == _selected); }
        finally { _updating = false; }
    }
}
