using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

/// <summary>A bounded selected-event neighborhood. Geometry and text are cached outside rendering.</summary>
public sealed class CausalityGraph : Control
{
    private sealed record Node(TraceEvent Event, Rect Bounds, FormattedText Title, FormattedText Detail);
    private sealed record EdgeVisual(StreamGeometry Geometry, Pen Pen, Point Tip);
    private readonly List<Node> _nodes = [];
    private readonly List<EdgeVisual> _edges = [];
    private TraceView? _trace;
    private Dictionary<long, TraceEvent> _lookup = [];
    private Dictionary<long, CausalEdge[]> _incoming = [], _outgoing = [];
    private long _selection;
    private Vector _offset = new(24, 48);
    private double _zoom = .85;
    private Point? _drag;
    private Vector _startOffset;
    private bool _moved;
    private static readonly Pen ArrowPen = new(ChartPalette.Muted, 1);
    public event Action<TraceEvent>? EventSelected;
    public CausalityGraph() { ClipToBounds = true; MinHeight = 280; }
    public void SetTrace(TraceView trace, AnalysisResult analysis, long selectedEvent = 0)
    {
        _trace = trace; _selection = selectedEvent;
        _lookup = trace.Events.ToDictionary(e => e.Id);
        _incoming = analysis.Edges.GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.ToArray());
        _outgoing = analysis.Edges.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.ToArray());
        Rebuild();
    }
    public void SelectEvent(long id) { if (_selection == id) return; _selection = id; Rebuild(); }
    public void Fit() { _zoom = .85; _offset = new(24, 56); InvalidateVisual(); }
    private void Rebuild()
    {
        _nodes.Clear(); _edges.Clear();
        if (_trace is null || _trace.Events.Length == 0) { InvalidateVisual(); return; }
        var selected = _lookup.GetValueOrDefault(_selection);
        if (selected.Id == 0) selected = _trace.Events.MaxBy(e => e.Duration);
        var included = new Dictionary<long, int> { [selected.Id] = 0 };
        var queue = new Queue<(long Id, int Depth)>(); queue.Enqueue((selected.Id, 0));
        while (queue.Count > 0 && included.Count < 120)
        {
            var (id, depth) = queue.Dequeue();
            foreach (var edge in _incoming.GetValueOrDefault(id) ?? [])
                if (depth > -5 && included.Count < 120 && _lookup.ContainsKey(edge.Source) && included.TryAdd(edge.Source, depth - 1)) queue.Enqueue((edge.Source, depth - 1));
            foreach (var edge in _outgoing.GetValueOrDefault(id) ?? [])
                if (depth < 5 && included.Count < 120 && _lookup.ContainsKey(edge.Target) && included.TryAdd(edge.Target, depth + 1)) queue.Enqueue((edge.Target, depth + 1));
        }
        var minimum = included.Values.Min(); var nodesById = new Dictionary<long, Node>();
        foreach (var column in included.GroupBy(pair => pair.Value).OrderBy(group => group.Key))
        {
            var row = 0;
            foreach (var pair in column.OrderBy(pair => _lookup[pair.Key].Timestamp))
            {
                var e = _lookup[pair.Key]; var title = ChartPalette.ShortName(_trace.Name(e));
                if (title.Length > 29) title = title[..28] + "…";
                var node = new Node(e, new((column.Key - minimum) * 246, row++ * 90, 208, 64), ChartPalette.Text(title, 11),
                    ChartPalette.Text($"{_trace.Milliseconds(e.Duration):F3} ms · #{e.ObjectId} · T{e.ThreadId}", 10, ChartPalette.Muted));
                _nodes.Add(node); nodesById[e.Id] = node;
            }
        }
        foreach (var id in included.Keys)
        foreach (var edge in _outgoing.GetValueOrDefault(id) ?? [])
        {
            if (!nodesById.TryGetValue(edge.Source, out var from) || !nodesById.TryGetValue(edge.Target, out var to)) continue;
            var a = new Point(from.Bounds.Right, from.Bounds.Center.Y); var b = new Point(to.Bounds.Left, to.Bounds.Center.Y);
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(a, false);
                // A fixed cubic tessellation avoids version-specific geometry method names.
                var c1 = new Point(a.X + 30, a.Y); var c2 = new Point(b.X - 30, b.Y);
                for (var step = 1; step <= 24; step++)
                {
                    var t = step / 24d; var u = 1 - t;
                    path.LineTo(new(u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                        u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y));
                }
                path.EndFigure(false);
            }
            _edges.Add(new(geometry, new Pen(edge.Inferred ? ChartPalette.Muted : ChartPalette.For(from.Event.Subsystem), 1.3), b));
        }
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset)))
        {
            foreach (var edge in _edges)
            {
                context.DrawGeometry(null, edge.Pen, edge.Geometry);
                context.DrawLine(ArrowPen, new(edge.Tip.X - 5, edge.Tip.Y - 3), edge.Tip);
                context.DrawLine(ArrowPen, new(edge.Tip.X - 5, edge.Tip.Y + 3), edge.Tip);
            }
            foreach (var node in _nodes)
            {
                context.DrawRectangle(ChartPalette.Alternate, new Pen(node.Event.Id == _selection ? ChartPalette.Accent : ChartPalette.Grid.Brush, node.Event.Id == _selection ? 2 : 1), node.Bounds, 7, 7);
                context.FillRectangle(ChartPalette.For(node.Event.Subsystem), new(node.Bounds.X + 1, node.Bounds.Y + 12, 3, 40));
                context.DrawText(node.Title, node.Bounds.TopLeft + new Vector(13, 12)); context.DrawText(node.Detail, node.Bounds.TopLeft + new Vector(13, 36));
            }
        }
        context.FillRectangle(ChartPalette.Background, new Rect(0, 0, Bounds.Width, 34));
        ChartPalette.Label(context, "RECORDED CAUSAL NEIGHBORHOOD  ·  drag to pan, Ctrl+wheel to zoom", new(16, 12), 10, ChartPalette.Muted);
        if (_nodes.Count == 0) ChartPalette.Label(context, "Select a recorded event to inspect its causal chain.", new(24, 80), 13);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { Fit(); return; }
        _drag = e.GetPosition(this); _startOffset = _offset; _moved = false; e.Pointer.Capture(this);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (_drag is not { } start) return;
        var delta = e.GetPosition(this) - start; _moved |= delta.Length > 3; _offset = _startOffset + delta; InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); if (_drag is null) return;
        if (!_moved)
        {
            var position = (e.GetPosition(this) - _offset) / _zoom;
            var node = _nodes.LastOrDefault(n => n.Bounds.Contains(position));
            if (node is not null) { _selection = node.Event.Id; EventSelected?.Invoke(node.Event); InvalidateVisual(); }
        }
        _drag = null; e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var position = e.GetPosition(this); var world = (position - _offset) / _zoom;
        _zoom = Math.Clamp(_zoom * Math.Pow(1.15, e.Delta.Y), .2, 3); _offset = position - world * _zoom; InvalidateVisual(); e.Handled = true;
    }
}
