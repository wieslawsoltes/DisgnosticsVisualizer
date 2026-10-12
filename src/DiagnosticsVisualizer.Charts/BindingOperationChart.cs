using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Invocation intervals on the captured clock. Gaps are not fabricated binding latency.</summary>
public sealed class BindingOperationChart : Control
{
    private const int Limit = 96;
    private BindingOperation[] _rows = [];
    private FormattedText[] _labels = [];
    private FormattedText _caption = ChartPalette.Text("Select a binding invocation", 10, ChartPalette.Muted);
    private long _start, _selected;
    private double _duration = 1;
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#EC8DA4"));
    public event Action<BindingOperation>? OperationSelected;
    public BindingOperationChart()
    {
        Height = 48; ClipToBounds = true; Focusable = true; VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        AutomationProperties.SetName(this, "Binding invocation timeline");
    }
    public void SetOperations(BindingOperation[] rows, TraceView? trace)
    {
        _rows = rows.Take(Limit).ToArray();
        _start = _rows.Select(r => r.Timestamp).DefaultIfEmpty(0).Min();
        _duration = Math.Max(1, _rows.Select(r => r.End?.End ?? r.Timestamp).DefaultIfEmpty(_start).Max() - (double)_start);
        _labels = _rows.Select(r => ChartPalette.Text($"{r.Kind} · expression #{r.ExpressionId} · {r.Status}", 10)).ToArray();
        _caption = ChartPalette.Text($"{_rows.Length:N0}/{rows.Length:N0} displayed calls · {_duration * 1000 / (trace?.Frequency ?? 1):N3} ms elapsed · delivery is not a winning value", 10, ChartPalette.Muted);
        Height = Math.Max(48, 30 + _rows.Length * 24); InvalidateVisual();
    }
    public void Select(long id) { _selected = id; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        using (context.PushClip(new Rect(8, 0, Math.Max(0, Bounds.Width - 16), 28))) context.DrawText(_caption, new Point(10, 8));
        var left = Math.Clamp(Bounds.Width * .50, 150, 400); var width = Math.Max(1, Bounds.Width - left - 12);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i]; var y = 30 + i * 24;
            if (row.Id == _selected) context.FillRectangle(ChartPalette.Selection, new Rect(0, y, Bounds.Width, 23));
            else if ((i & 1) == 0) context.FillRectangle(ChartPalette.Alternate, new Rect(0, y, Bounds.Width, 23));
            using (context.PushClip(new Rect(8, y, Math.Max(0, left - 16), 23))) context.DrawText(_labels[i], new Point(10, y + 4));
            if (row.DurationMs is null || row.End is not { } span) continue;
            var x = left + Math.Clamp((span.Timestamp - (double)_start) / _duration, 0, 1) * width;
            var size = Math.Clamp(span.Duration / _duration, 0, 1) * width;
            var brush = row.Status == "Threw" || row.Error is "Binding error" or "Validation error" ? ErrorBrush : ChartPalette.Accent;
            context.FillRectangle(brush, new Rect(x, y + 5, Math.Max(1, size), 13));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var index = (int)Math.Floor((e.GetPosition(this).Y - 30) / 24);
        if (index < 0 || index >= _rows.Length) return;
        Focus(); Choose(index); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_rows.Length == 0 || e.Key is not (Key.Up or Key.Down or Key.Home or Key.End)) return;
        var current = Array.FindIndex(_rows, r => r.Id == _selected);
        Choose(e.Key switch { Key.Home => 0, Key.End => _rows.Length - 1, _ => Math.Clamp(current + (e.Key == Key.Down ? 1 : -1), 0, _rows.Length - 1) }); e.Handled = true;
    }
    private void Choose(int index) { Select(_rows[index].Id); OperationSelected?.Invoke(_rows[index]); }
}
