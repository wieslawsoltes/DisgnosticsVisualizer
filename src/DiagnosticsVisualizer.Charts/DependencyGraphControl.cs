using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Deterministic, bounded incoming/selected/outgoing graph. The accompanying table exposes every edge.</summary>
public sealed class DependencyGraphControl : Control
{
    private DependencyNeighborhood? _graph;
    private readonly Dictionary<long, (FormattedText Title, FormattedText Detail)> _labels = [];
    private readonly Dictionary<long, Rect> _bounds = [];
    public event Action<long>? ObjectSelected;
    public DependencyGraphControl() { MinHeight = 250; ClipToBounds = true; Focusable = true; }
    public void SetGraph(DependencyNeighborhood graph)
    {
        _graph = graph; _labels.Clear();
        foreach (var node in graph.Nodes)
            _labels[node.Id] = (ChartPalette.Text(node.Label, 11, ChartPalette.Foreground), ChartPalette.Text($"#{node.Id} · {node.Type}", 9, ChartPalette.Muted));
        var incoming = graph.Nodes.Count(n => n.Id != graph.Selected && !graph.Edges.Any(e => e.Source == graph.Selected && e.Target == n.Id));
        Height = Math.Max(250, Math.Max(incoming, graph.Nodes.Length - 1 - incoming) * 62 + 54);
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size)); _bounds.Clear();
        if (_graph is not { Nodes.Length: > 0 } graph) return;
        var col = Math.Max(1, (Bounds.Width - 48) / 3); var width = Math.Max(1, col - 18);
        var incoming = graph.Nodes.Where(n => n.Id != graph.Selected && !graph.Edges.Any(e => e.Source == graph.Selected && e.Target == n.Id)).ToArray();
        var outgoing = graph.Nodes.Where(n => n.Id != graph.Selected && !incoming.Any(x => x.Id == n.Id)).ToArray();
        _bounds[graph.Selected] = new(24 + col, Bounds.Height / 2 - 23, width, 46);
        void Place(DependencyNode[] nodes, int column)
        { for (var i = 0; i < nodes.Length; i++) _bounds[nodes[i].Id] = new(24 + col * column, (Bounds.Height - nodes.Length * 62) / 2 + i * 62, width, 46); }
        Place(incoming, 0); Place(outgoing, 2);
        foreach (var edge in graph.Edges)
        {
            if (!_bounds.TryGetValue(edge.Source, out var source) || !_bounds.TryGetValue(edge.Target, out var target)) continue;
            var pen = new Pen(ChartPalette.For(edge.Kind.Contains("Visual", StringComparison.Ordinal) ? Subsystem.VisualTree :
                edge.Kind.Contains("Logical", StringComparison.Ordinal) ? Subsystem.LogicalTree : Subsystem.Resources), 1.3);
            if (edge.Source == edge.Target) { context.DrawEllipse(null, pen, new Rect(source.X + 20, source.Y - 16, 28, 22)); continue; }
            var rightward = target.Center.X > source.Center.X;
            var from = new Point(rightward ? source.Right : source.Left, source.Center.Y);
            var to = new Point(rightward ? target.Left : target.Right, target.Center.Y);
            var middle = (from.X + to.X) / 2;
            context.DrawLine(pen, from, new(middle, from.Y)); context.DrawLine(pen, new(middle, from.Y), new(middle, to.Y)); context.DrawLine(pen, new(middle, to.Y), to);
            var direction = rightward ? -1 : 1;
            context.DrawLine(pen, to, new(to.X + direction * 5, to.Y - 3)); context.DrawLine(pen, to, new(to.X + direction * 5, to.Y + 3));
        }
        foreach (var node in graph.Nodes)
        {
            var rect = _bounds[node.Id];
            context.DrawRectangle(node.Id == graph.Selected ? ChartPalette.Selection : ChartPalette.Alternate,
                node.Id == graph.Selected ? new Pen(ChartPalette.Accent, 1.5) : ChartPalette.Grid, rect, 5, 5);
            using (context.PushClip(rect.Deflate(6)))
            { var label = _labels[node.Id]; context.DrawText(label.Title, new(rect.X + 8, rect.Y + 7)); context.DrawText(label.Detail, new(rect.X + 8, rect.Y + 26)); }
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        foreach (var (id, rect) in _bounds)
            if (rect.Contains(e.GetPosition(this))) { ObjectSelected?.Invoke(id); e.Handled = true; break; }
    }
}
