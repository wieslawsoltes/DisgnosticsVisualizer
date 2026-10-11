using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;

namespace DiagnosticsVisualizer.Charts;

/// <summary>A bounded waterfall of completed operation lifetimes, separate from thread CPU lanes.</summary>
public sealed class AsyncLifetimeChart : Control
{
    private AsyncOperation[] _rows = [];
    private FormattedText[] _labels = [];
    private readonly List<(Rect Bounds, AsyncOperation Operation)> _hits = [];
    private long _start, _end;
    private FormattedText? _caption;
    public event Action<AsyncOperation>? OperationSelected;
    public AsyncLifetimeChart() { Height = 176; ClipToBounds = true; Focusable = true; }
    public void SetOperations(AsyncOperation[] operations, TraceView trace)
    {
        _rows = operations.Where(o => o.IsComplete).Take(6).ToArray();
        _labels = _rows.Select(o => ChartPalette.Text($"{o.DurationMs:N2} ms · {o.Status}", 10, ChartPalette.Foreground)).ToArray();
        _start = _rows.Length == 0 ? 0 : _rows.Min(o => o.Begin!.Value.Timestamp);
        _end = _rows.Length == 0 ? 1 : _rows.Max(o => o.Completion!.Value.Timestamp);
        _caption = ChartPalette.Text(_rows.Length == 0 ? "No complete async lifetimes recorded" :
            $"LONGEST LIFETIMES · {trace.Milliseconds(_start - trace.OriginTimestamp):N2}–{trace.Milliseconds(_end - trace.OriginTimestamp):N2} ms · includes await suspension", 10, ChartPalette.Muted);
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Alternate, new Rect(Bounds.Size)); _hits.Clear();
        if (_caption is not null) context.DrawText(_caption, new(12, 8));
        var width = Math.Max(1, Bounds.Width - 170); var span = Math.Max(1, _end - (double)_start);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i]; var y = 30 + i * 23;
            context.DrawText(_labels[i], new(12, y + 1));
            var x = 160 + (row.Begin!.Value.Timestamp - (double)_start) / span * width;
            var end = 160 + (row.Completion!.Value.Timestamp - (double)_start) / span * width;
            var rect = new Rect(x, y, Math.Max(2, end - x), 17);
            context.DrawRectangle(ChartPalette.For(row.Subsystem), null, rect, 3, 3);
            _hits.Add((rect, row));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        foreach (var hit in _hits)
            if (hit.Bounds.Contains(e.GetPosition(this))) { OperationSelected?.Invoke(hit.Operation); e.Handled = true; break; }
    }
}
