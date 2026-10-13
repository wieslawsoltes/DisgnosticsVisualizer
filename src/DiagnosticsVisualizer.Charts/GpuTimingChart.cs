using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Duration comparison only: these bars are not placed at CPU timestamps.</summary>
public sealed class GpuTimingChart : Control
{
    private GpuTimingRow[] _rows = [];
    private FormattedText[] _labels = [];
    private double _maximum = 1;
    private long _selected;
    public event Action<GpuTimingRow>? QuerySelected;
    public GpuTimingChart()
    { ClipToBounds = true; Focusable = true; MinHeight = 80; AutomationProperties.SetName(this, "GPU device interval comparison, not CPU timeline"); }
    public void SetRows(IEnumerable<GpuTimingRow> rows)
    {
        _rows = rows.Where(r => r.IsAvailable).OrderByDescending(r => r.GpuMs).Take(8).ToArray();
        _labels = _rows.Select((r, i) => ChartPalette.Text($"{i + 1}. ctx {r.ContextId} · {r.GpuMs:N3} ms", 11)).ToArray();
        _maximum = Math.Max(.000001, _rows.Select(r => r.GpuMs ?? 0).DefaultIfEmpty(0).Max()); InvalidateVisual();
    }
    public void Select(long id) { _selected = id; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Alternate, new Rect(Bounds.Size));
        ChartPalette.Label(context, _rows.Length == 0 ? "No available device intervals" : "GPU DEVICE INTERVALS · longest 8 · duration comparison only", new(12, 8), 10, ChartPalette.Muted);
        var left = Math.Min(200, Math.Max(110, Bounds.Width * .25)); var width = Math.Max(1, Bounds.Width - left - 14);
        for (var i = 0; i < _rows.Length && 32 + i * 23 < Bounds.Height; i++)
        {
            var y = 31 + i * 23;
            if (_selected == _rows[i].Id) context.FillRectangle(ChartPalette.Selection, new Rect(0, y - 2, Bounds.Width, 22));
            using (context.PushClip(new Rect(10, y, Math.Max(1, left - 20), 22))) context.DrawText(_labels[i], new(12, y));
            context.FillRectangle(ChartPalette.Accent, new Rect(left, y + 2, Math.Max(1, (_rows[i].GpuMs ?? 0) / _maximum * width), 15));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var i = (int)((e.GetPosition(this).Y - 29) / 23);
        if (e.GetPosition(this).Y >= 29 && (uint)i < (uint)_rows.Length)
        { Focus(); Select(_rows[i].Id); QuerySelected?.Invoke(_rows[i]); e.Handled = true; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_rows.Length == 0 || e.Key is not (Key.Up or Key.Down)) return;
        var i = Array.FindIndex(_rows, r => r.Id == _selected);
        i = Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, _rows.Length - 1);
        Select(_rows[i].Id); QuerySelected?.Invoke(_rows[i]); e.Handled = true;
    }
}
