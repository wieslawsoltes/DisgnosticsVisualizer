using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

/// <summary>Recorded visual-tree layout rectangles; does not invent transforms or clipping absent from a snapshot.</summary>
public sealed class LayoutSceneControl : Control
{
    private sealed record Box(NodeSnapshot Node, Rect Bounds, int Depth);
    private readonly List<Box> _boxes = [];
    private readonly List<(Rect Screen, long Id)> _hits = [];
    private long _selected;
    private Rect _extent = new(0, 0, 1, 1);
    private static readonly Pen Outline = new(ChartPalette.Muted, .6);
    private static readonly Pen Selected = new(ChartPalette.Accent, 2);
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#124C9FFF"));
    public event Action<long>? ObjectSelected;
    public LayoutSceneControl() { MinHeight = 260; ClipToBounds = true; }
    public void SetSnapshot(SnapshotData snapshot, long selected)
    {
        _selected = selected; _boxes.Clear();
        var nodes = snapshot.Nodes.ToDictionary(n => n.Id); var resolved = new Dictionary<long, (Point Position, int Depth)>();
        foreach (var node in snapshot.Nodes)
        {
            if (node.Width <= 0 || node.Height <= 0 || !node.Visible) continue;
            var seen = new HashSet<long>(); var path = new Stack<NodeSnapshot>(); var current = node; var valid = true;
            while (!resolved.ContainsKey(current.Id))
            {
                if (!seen.Add(current.Id) || path.Count > 256) { valid = false; break; }
                path.Push(current);
                if (current.VisualParent == 0 || !nodes.TryGetValue(current.VisualParent, out current)) break;
            }
            if (!valid) continue;
            while (path.TryPop(out var item))
            {
                var parent = resolved.GetValueOrDefault(item.VisualParent);
                resolved[item.Id] = (parent.Position + new Vector(item.X, item.Y), parent.Depth + 1);
            }
            var position = resolved[node.Id];
            _boxes.Add(new(node, new(position.Position, new Size(node.Width, node.Height)), position.Depth));
        }
        _boxes.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));
        _extent = _boxes.Count == 0 ? new(0, 0, 1, 1) : _boxes.Select(b => b.Bounds).Aggregate((a, b) => a.Union(b));
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size)); _hits.Clear();
        ChartPalette.Label(context, "LAYOUT GEOMETRY  ·  click a control to inspect", new(14, 10), 11, ChartPalette.Muted);
        ChartPalette.Label(context, "Recorded bounds only; render transforms and clipping are not captured.", new(14, 29), 10, ChartPalette.Muted);
        var scale = Math.Max(.001, Math.Min(Math.Max(1, Bounds.Width - 48) / Math.Max(1, _extent.Width), Math.Max(1, Bounds.Height - 90) / Math.Max(1, _extent.Height)));
        foreach (var box in _boxes)
        {
            var rect = new Rect(24 + (box.Bounds.X - _extent.X) * scale, 66 + (box.Bounds.Y - _extent.Y) * scale, box.Bounds.Width * scale, box.Bounds.Height * scale);
            context.DrawRectangle(Fill, Outline, rect); _hits.Add((rect, box.Node.Id));
        }
        foreach (var hit in _hits.Where(h => h.Id == _selected)) context.DrawRectangle(null, Selected, hit.Screen);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        for (var i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Screen.Contains(point))
        { _selected = _hits[i].Id; ObjectSelected?.Invoke(_selected); InvalidateVisual(); e.Handled = true; return; }
    }
}
