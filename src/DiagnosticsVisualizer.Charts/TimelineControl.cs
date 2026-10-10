using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

public enum TimelineGrouping { Subsystem, ThreadFlame }

/// <summary>Single retained visual; viewport interval queries and explicit density fallback avoid per-event controls.</summary>
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<TraceView?> TraceProperty = AvaloniaProperty.Register<TimelineControl, TraceView?>(nameof(Trace));
    public static readonly StyledProperty<long> SelectedEventIdProperty = AvaloniaProperty.Register<TimelineControl, long>(nameof(SelectedEventId));
    public static readonly StyledProperty<long> SelectedObjectIdProperty = AvaloniaProperty.Register<TimelineControl, long>(nameof(SelectedObjectId));
    public static readonly StyledProperty<TimelineGrouping> GroupingProperty = AvaloniaProperty.Register<TimelineControl, TimelineGrouping>(nameof(Grouping));
    private readonly List<(Rect Bounds, TraceEvent Event)> _hits = [];
    private readonly Dictionary<long, int> _depths = [];
    private readonly Dictionary<string, FormattedText> _labels = new(StringComparer.Ordinal);
    private IntervalIndex? _index;
    private Dictionary<Subsystem, int> _categoryRows = [];
    private Dictionary<int, int> _threadRows = [];
    private int[,]? _density;
    private long _traceStart, _traceEnd = 1;
    private double _start, _end = 1;
    private Point? _dragStart;
    private double _dragTimeStart, _dragTimeEnd;
    private bool _dragged;
    private int _rows;
    private const double Header = 34, Left = 156, Row = 35;
    private const int DensityBuckets = 2048;
    public TraceView? Trace { get => GetValue(TraceProperty); set => SetValue(TraceProperty, value); }
    public long SelectedEventId { get => GetValue(SelectedEventIdProperty); set => SetValue(SelectedEventIdProperty, value); }
    public long SelectedObjectId { get => GetValue(SelectedObjectIdProperty); set => SetValue(SelectedObjectIdProperty, value); }
    public TimelineGrouping Grouping { get => GetValue(GroupingProperty); set => SetValue(GroupingProperty, value); }
    public event Action<TraceEvent>? EventSelected;
    public event Action<double, double>? ViewportChanged;
    public int LastRenderedEvents { get; private set; }
    public bool DensityMode { get; private set; }
    static TimelineControl()
    {
        AffectsRender<TimelineControl>(SelectedEventIdProperty, SelectedObjectIdProperty);
        TraceProperty.Changed.AddClassHandler<TimelineControl>((control, _) => control.Rebuild());
        GroupingProperty.Changed.AddClassHandler<TimelineControl>((control, _) => control.Rebuild());
    }
    public TimelineControl() { Focusable = true; ClipToBounds = true; }
    private void Rebuild()
    {
        _labels.Clear(); _depths.Clear(); _hits.Clear();
        var events = Trace?.Events ?? [];
        _index = new(events); _traceStart = events.Length == 0 ? 0 : events.Min(e => e.Timestamp);
        _traceEnd = events.Length == 0 ? 1 : events.Max(e => e.End);
        if (_traceEnd <= _traceStart) _traceEnd = _traceStart + 1;
        _categoryRows = events.Select(e => e.Subsystem).Distinct().Order().Select((category, index) => (category, index)).ToDictionary(x => x.category, x => x.index);
        _threadRows = events.Select(e => e.ThreadId).Distinct().Order().Select((thread, index) => (thread, index)).ToDictionary(x => x.thread, x => x.index);
        if (Grouping == TimelineGrouping.ThreadFlame)
        {
            var lookup = events.ToDictionary(e => e.Id);
            foreach (var e in events)
            {
                var current = e; var depth = 0; var seen = new HashSet<long>();
                while (depth < 8 && current.ParentId != 0 && seen.Add(current.Id) && lookup.TryGetValue(current.ParentId, out var parent) && parent.ThreadId == e.ThreadId)
                { depth++; current = parent; }
                _depths[e.Id] = depth;
            }
        }
        _rows = Grouping == TimelineGrouping.Subsystem ? _categoryRows.Count : _threadRows.Count * 9;
        _density = new int[Math.Max(1, _categoryRows.Count), DensityBuckets];
        var span = _traceEnd - (double)_traceStart;
        foreach (var e in events)
        {
            var bucket = Math.Clamp((int)((e.Timestamp - (double)_traceStart) / span * DensityBuckets), 0, DensityBuckets - 1);
            var row = _categoryRows[e.Subsystem]; if (_density[row, bucket] < int.MaxValue) _density[row, bucket]++;
        }
        Fit(); InvalidateMeasure();
    }
    public void Fit()
    {
        _start = _traceStart; _end = _traceEnd;
        var padding = Math.Max(1, (_end - _start) * 0.018); _start -= padding; _end += padding;
        InvalidateVisual(); ViewportChanged?.Invoke(_start, _end);
    }
    public void FocusEvent(TraceEvent e)
    {
        SelectedEventId = e.Id;
        var span = Math.Max(e.Duration * 4d, (Trace?.Frequency ?? 1_000_000) * .025);
        _start = e.Timestamp - span * .1; _end = _start + span; InvalidateVisual(); ViewportChanged?.Invoke(_start, _end);
    }
    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 1000, Math.Max(260, Header + Math.Max(1, _rows) * Row));
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size)); _hits.Clear(); LastRenderedEvents = 0;
        if (Trace is not { } trace || _index is null || trace.Events.Length == 0)
        { ChartPalette.Label(context, "Open a trace or connect to an instrumented application.", new(24, 70), 13, ChartPalette.Muted); return; }
        var width = Math.Max(1, Bounds.Width - Left - 12); var scale = width / Math.Max(1, _end - _start);
        for (var row = 0; row < _rows; row++)
        {
            var y = Header + row * Row;
            if ((row & 1) == 1) context.FillRectangle(ChartPalette.Alternate, new(0, y, Bounds.Width, Row));
            context.DrawLine(ChartPalette.Grid, new(0, y + Row), new(Bounds.Width, y + Row));
        }
        var nice = NiceStep((_end - _start) / Math.Max(2, width / 120));
        for (var tick = Math.Floor(_start / nice) * nice; tick <= _end; tick += nice)
        {
            var x = Left + (tick - _start) * scale;
            if (x < Left) continue;
            context.DrawLine(ChartPalette.Grid, new(x, Header), new(x, Bounds.Height));
            ChartPalette.Label(context, $"{trace.Milliseconds((long)(tick - trace.OriginTimestamp)):0.##} ms", new(x + 5, 9), 10, ChartPalette.Muted);
        }
        if (Grouping == TimelineGrouping.Subsystem)
            foreach (var (category, row) in _categoryRows)
            {
                context.FillRectangle(ChartPalette.For(category), new(12, Header + row * Row + 12, 4, 12));
                ChartPalette.Label(context, category.ToString(), new(24, Header + row * Row + 10), 11);
            }
        else
            foreach (var (thread, row) in _threadRows)
                ChartPalette.Label(context, $"Thread {thread}", new(15, Header + row * 9 * Row + 10), 11);
        context.DrawLine(ChartPalette.Grid, new(Left, 0), new(Left, Bounds.Height));
        var visible = _index.Query(ClampLong(_start), ClampLong(_end), 50_001);
        DensityMode = visible.Count > 50_000 && Grouping == TimelineGrouping.Subsystem;
        using (context.PushClip(new Rect(Left, Header, width, Math.Max(0, Bounds.Height - Header))))
        {
            if (DensityMode) DrawDensity(context, scale);
            else
            {
                foreach (var e in visible.Take(50_000))
                {
                    var row = Grouping == TimelineGrouping.Subsystem ? _categoryRows[e.Subsystem] : _threadRows[e.ThreadId] * 9 + _depths.GetValueOrDefault(e.Id);
                    var x = Left + (e.Timestamp - _start) * scale;
                    var right = Left + (e.End - _start) * scale;
                    var rect = new Rect(Math.Max(Left, x), Header + row * Row + 7, Math.Max(1.5, Math.Min(Left + width, right) - Math.Max(Left, x)), Row - 14);
                    var selected = e.Id == SelectedEventId;
                    var related = SelectedObjectId != 0 && e.ObjectId == SelectedObjectId;
                    context.DrawRectangle(ChartPalette.For(e.Subsystem), selected ? new Pen(Brushes.White, 2) : related ? new Pen(ChartPalette.Accent, 1.5) : null, rect, 3, 3);
                    if (rect.Width >= 54)
                    {
                        var label = Label(trace.Name(e));
                        using (context.PushClip(rect.Deflate(3))) context.DrawText(label, new(rect.X + 5, rect.Y + 3));
                    }
                    _hits.Add((rect, e)); LastRenderedEvents++;
                }
            }
        }
        ChartPalette.Label(context, DensityMode ? "EVENT DENSITY" : "CPU SPANS", new(14, 10), 10, ChartPalette.Muted);
        if (visible.Count > 50_000) ChartPalette.Label(context, DensityMode ? "Density overview · zoom in for individual spans" : "Span display budget reached · zoom in", new(Left + 12, Header + 4), 11);
    }
    private void DrawDensity(DrawingContext context, double scale)
    {
        if (_density is null) return;
        var step = (_traceEnd - (double)_traceStart) / DensityBuckets;
        foreach (var (category, row) in _categoryRows)
        for (var bucket = 0; bucket < DensityBuckets; bucket++)
        {
            var count = _density[row, bucket]; if (count == 0) continue;
            var x = Left + (_traceStart + bucket * step - _start) * scale;
            var width = Math.Max(1, step * scale);
            if (x + width < Left || x > Bounds.Width) continue;
            var height = Math.Min(Row - 8, 3 + Math.Log2(count + 1) * 4);
            context.FillRectangle(ChartPalette.For(category), new(x, Header + row * Row + Row - height - 4, width, height));
        }
    }
    private FormattedText Label(string name)
    {
        if (_labels.TryGetValue(name, out var label)) return label;
        if (_labels.Count > 4096) _labels.Clear();
        return _labels[name] = ChartPalette.Text(ChartPalette.ShortName(name), 10, new SolidColorBrush(Color.Parse("#102033")));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
        _dragStart = e.GetPosition(this); _dragTimeStart = _start; _dragTimeEnd = _end; _dragged = false;
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var position = e.GetPosition(this);
        if (_dragStart is { } start)
        {
            var delta = position.X - start.X; _dragged |= Math.Abs(delta) > 3;
            var ticks = delta / Math.Max(1, Bounds.Width - Left - 12) * (_dragTimeEnd - _dragTimeStart);
            _start = _dragTimeStart - ticks; _end = _dragTimeEnd - ticks; InvalidateVisual(); return;
        }
        var hit = Hit(position);
        ToolTip.SetTip(this, hit is { } value && Trace is { } trace ? $"{trace.Name(value)}\n{trace.Milliseconds(value.Duration):F3} ms · thread {value.ThreadId} · object #{value.ObjectId}\nEvent #{value.Id} · parent #{value.ParentId}" : null);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;
        if (!_dragged && Hit(e.GetPosition(this)) is { } hit) { SelectedEventId = hit.Id; EventSelected?.Invoke(hit); }
        _dragStart = null; e.Pointer.Capture(null); ViewportChanged?.Invoke(_start, _end); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _dragStart = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var ratio = Math.Clamp((e.GetPosition(this).X - Left) / Math.Max(1, Bounds.Width - Left - 12), 0, 1);
        var point = _start + ratio * (_end - _start);
        var span = Math.Clamp((_end - _start) * Math.Pow(1.22, -e.Delta.Y), 1, Math.Max(1, (_traceEnd - (double)_traceStart) * 20));
        _start = point - ratio * span; _end = _start + span; InvalidateVisual(); ViewportChanged?.Invoke(_start, _end); e.Handled = true;
    }
    private TraceEvent? Hit(Point point)
    {
        for (var i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Bounds.Contains(point)) return _hits[i].Event;
        return null;
    }
    private static double NiceStep(double value)
    {
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, value)))); var fraction = value / exponent;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * exponent;
    }
    private static long ClampLong(double value) => value <= long.MinValue ? long.MinValue : value >= long.MaxValue ? long.MaxValue : (long)value;
}
