using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Discrete finite scalar notification observations. Never interpolates target state or plots requested assignments as effective values.</summary>
public sealed class PropertyValueChart : Control
{
    private const int Limit = 512;
    private (PropertyOperation Row, double Value)[] _points = [];
    private readonly List<(Point Point, PropertyOperation Row)> _hits = [];
    private long _selected, _start;
    private double _duration = 1, _minimum, _maximum = 1, _frequency = 1;
    private FormattedText _caption = ChartPalette.Text("No finite scalar notifications; values are private by default", 10, ChartPalette.Muted);
    public event Action<PropertyOperation>? OperationSelected;
    public int ObservationCount => _points.Length;
    public PropertyValueChart() { Height = 180; MinHeight = 120; ClipToBounds = true; Focusable = true; AutomationProperties.SetName(this, "Discrete effective and base property-value observations"); }
    public void SetOperations(PropertyOperation[] rows, TraceView? trace)
    {
        _frequency = trace?.Frequency ?? 1;
        _points = rows.Where(r => r.IsComplete && (r.Effective || r.BaseValue)).Select(r => (Row: r, Value: Numeric(r.NewValue)))
            .Where(p => p.Value.HasValue && double.IsFinite(p.Value.Value)).Take(Limit).Select(p => (p.Row, p.Value!.Value)).ToArray();
        _start = _points.Select(p => p.Row.Timestamp).DefaultIfEmpty(0).Min();
        _duration = Math.Max(1, _points.Select(p => (double)p.Row.Timestamp - _start).DefaultIfEmpty(0).Max());
        _minimum = _points.Select(p => p.Value).DefaultIfEmpty(0).Min(); _maximum = _points.Select(p => p.Value).DefaultIfEmpty(1).Max();
        if (_minimum == _maximum) { _minimum -= .5; _maximum += .5; }
        _caption = ChartPalette.Text(_points.Length == 0 ? "No finite scalar notifications; enable AVTRACE_PROPERTY_SCALARS=1 explicitly" :
            $"{_points.Length:N0} points (limit {Limit}) · solid: effective · outlined: base · no interpolation", 10, ChartPalette.Muted); InvalidateVisual();
    }
    private static double? Numeric(PropertyValue value) => value.Kind switch
    { PropertyValueKind.Signed => value.Bits, PropertyValueKind.Unsigned => unchecked((ulong)value.Bits), PropertyValueKind.Boolean => value.Bits,
        PropertyValueKind.Float64 => BitConverter.Int64BitsToDouble(value.Bits), PropertyValueKind.Float32 => BitConverter.Int32BitsToSingle((int)value.Bits), _ => null };
    public void Select(long id) { _selected = id; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        _hits.Clear(); context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        using (context.PushClip(new Rect(8, 0, Math.Max(0, Bounds.Width - 16), 30))) context.DrawText(_caption, new Point(10, 8));
        if (_points.Length == 0 || Bounds.Width < 100) return;
        const double left = 75; var width = Math.Max(1, Bounds.Width - left - 20); var top = 38; var height = Math.Max(1, Bounds.Height - 70);
        var pen = new Pen(ChartPalette.Muted, .5);
        for (var n = 0; n < 3; n++)
        {
            var y = top + height * n / 2; context.DrawLine(pen, new Point(left, y), new Point(left + width, y));
            ChartPalette.Label(context, (_maximum * (1 - n / 2d) + _minimum * (n / 2d)).ToString("G4"), new Point(8, y - 5), 10, ChartPalette.Muted);
        }
        ChartPalette.Label(context, $"0 → {_duration * 1000 / _frequency:N3} ms from first retained point", new Point(left, top + height + 12), 10, ChartPalette.Muted);
        foreach (var (row, value) in _points)
        {
            // Normalize before subtraction when very large finite scalar endpoints would overflow.
            var scale = Math.Max(1, Math.Max(Math.Abs(_minimum), Math.Abs(_maximum)));
            var fraction = (value / scale - _minimum / scale) / (_maximum / scale - _minimum / scale);
            if (!double.IsFinite(fraction)) fraction = .5;
            var point = new Point(left + ((double)row.Timestamp - _start) / _duration * width, top + height * (1 - fraction));
            var radius = row.Id == _selected ? 5 : 3;
            context.DrawEllipse(row.Effective ? ChartPalette.Accent : null, new Pen(row.Id == _selected ? ChartPalette.Foreground : ChartPalette.Accent, 1.5), point, radius, radius);
            _hits.Add((point, row));
        }
    }
    private static double DistanceSquared(Point a, Point b) { var x = a.X - b.X; var y = a.Y - b.Y; return x * x + y * y; }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _hits.Count == 0) return;
        var p = e.GetPosition(this); var hit = _hits.MinBy(h => DistanceSquared(h.Point, p));
        if (DistanceSquared(hit.Point, p) > 144) return;
        Focus(); Select(hit.Row.Id); OperationSelected?.Invoke(hit.Row); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_points.Length == 0 || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var current = Array.FindIndex(_points, p => p.Row.Id == _selected);
        var index = e.Key switch { Key.Home => 0, Key.End => _points.Length - 1, _ => Math.Clamp(current + (e.Key == Key.Right ? 1 : -1), 0, _points.Length - 1) };
        var row = _points[index].Row; Select(row.Id); OperationSelected?.Invoke(row); e.Handled = true;
    }
}
