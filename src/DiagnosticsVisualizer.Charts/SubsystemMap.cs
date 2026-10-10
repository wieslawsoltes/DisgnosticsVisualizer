using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

public sealed class SubsystemMap : Control
{
    private TraceView? _trace;
    private readonly Dictionary<Subsystem, long> _counts = [];
    private readonly Dictionary<(Subsystem From, Subsystem To), int> _edges = [];
    private readonly Dictionary<Subsystem, Rect> _boxes = [];
    public event Action<Subsystem>? SubsystemSelected;
    public SubsystemMap() { MinHeight = 500; ClipToBounds = true; }
    public void SetTrace(TraceView trace, AnalysisResult analysis)
    {
        _trace = trace; _counts.Clear(); _edges.Clear(); var events = trace.Events.ToDictionary(e => e.Id);
        foreach (var e in trace.Events) _counts[e.Subsystem] = _counts.GetValueOrDefault(e.Subsystem) + 1;
        foreach (var edge in analysis.Edges)
            if (events.TryGetValue(edge.Source, out var from) && events.TryGetValue(edge.Target, out var to) && from.Subsystem != to.Subsystem)
                _edges[(from.Subsystem, to.Subsystem)] = _edges.GetValueOrDefault((from.Subsystem, to.Subsystem)) + 1;
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size)); _boxes.Clear();
        ChartPalette.Label(context, "SUBSYSTEM DEPENDENCIES  /  RECORDED CROSS-SUBSYSTEM PARENT LINKS", new(18, 16), 10, ChartPalette.Muted);
        var categories = Enum.GetValues<Subsystem>(); var columns = Bounds.Width > 850 ? 5 : Bounds.Width > 560 ? 4 : 3;
        var rows = (categories.Length + columns - 1) / columns; var cellWidth = Math.Max(100, (Bounds.Width - 32) / columns);
        var cellHeight = Math.Max(75, (Bounds.Height - 74) / rows);
        for (var i = 0; i < categories.Length; i++) _boxes[categories[i]] = new(16 + i % columns * cellWidth + 6, 56 + i / columns * cellHeight + 8, cellWidth - 14, cellHeight - 20);
        foreach (var (edge, count) in _edges.OrderBy(edge => edge.Value))
        {
            var from = _boxes[edge.From].Center; var to = _boxes[edge.To].Center;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#354863")), Math.Clamp(Math.Log2(count + 1) * .3, .8, 3)), from, to);
        }
        foreach (var category in categories)
        {
            var box = _boxes[category]; var count = _counts.GetValueOrDefault(category);
            context.DrawRectangle(count == 0 ? ChartPalette.Background : ChartPalette.Alternate, new Pen(count == 0 ? ChartPalette.Grid.Brush : ChartPalette.For(category), 1), box, 7, 7);
            ChartPalette.Label(context, category.ToString(), box.TopLeft + new Vector(10, 10), 10, count == 0 ? ChartPalette.Muted : ChartPalette.Foreground);
            ChartPalette.Label(context, count == 0 ? "not recorded" : $"{count:N0} events", box.TopLeft + new Vector(10, 30), 11, ChartPalette.For(category));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var position = e.GetPosition(this);
        foreach (var (category, box) in _boxes) if (box.Contains(position)) { SubsystemSelected?.Invoke(category); break; }
    }
}
