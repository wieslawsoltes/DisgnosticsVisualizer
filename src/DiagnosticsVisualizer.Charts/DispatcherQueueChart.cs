using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Admission-aligned, common-scale queue residence and callback durations. Not CPU utilization.</summary>
public sealed class DispatcherQueueChart : Control
{
    private static readonly IBrush RunnableBrush = new SolidColorBrush(Color.Parse("#F0BD65"));
    private DispatcherOperation[] _rows = [];
    private FormattedText[] _labels = [];
    private readonly List<(Rect Bounds, DispatcherOperation Row)> _hits = [];
    private double _maximum = 1, _frequency = 1;
    private long _selected;
    public event Action<DispatcherOperation>? OperationSelected;
    public DispatcherQueueChart()
    {
        Height = 192; ClipToBounds = true; Focusable = true;
        AutomationProperties.SetName(this, "Dispatcher queue durations: orange runnable wait, gray inactive, blue callback");
    }
    public void SetOperations(DispatcherOperation[] operations, TraceView trace)
    {
        _rows = operations.Where(o => o.Queued is not null && o.QueueMs is not null).Take(6).ToArray();
        _labels = _rows.Select(o => ChartPalette.Text($"#{o.Id} · {o.QueueMs:N2} / {(o.ExecutionMs is { } duration ? duration.ToString("N2") : "—")} ms", 10)).ToArray();
        _maximum = Math.Max(.001, _rows.Select(o => o.QueueMs!.Value + (o.ExecutionMs ?? 0)).DefaultIfEmpty(0).Max());
        _frequency = trace.Frequency; InvalidateVisual();
    }
    public void Select(long id) { _selected = id; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Alternate, new Rect(Bounds.Size)); _hits.Clear();
        using (context.PushClip(new Rect(8, 0, Math.Max(0, Bounds.Width - 16), 28)))
            ChartPalette.Label(context, _rows.Length == 0 ? "No complete queue residence observed" :
                $"QUEUE / CALLBACK · 0–{_maximum:N2} ms · orange runnable · gray inactive · blue callback", new(12, 8), 10, ChartPalette.Muted);
        var left = Math.Min(220, Math.Max(130, Bounds.Width * .30)); var width = Math.Max(1, Bounds.Width - left - 12);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i]; var y = 33 + i * 25;
            if (row.Id == _selected) context.FillRectangle(ChartPalette.Selection, new Rect(0, y - 2, Bounds.Width, 23));
            using (context.PushClip(new Rect(8, y, Math.Max(0, left - 16), 21))) context.DrawText(_labels[i], new(12, y + 2));
            var queueMs = row.QueueMs!.Value;
            if (row.RunnableQueueMs is not null && row.Queued is { } queued)
            {
                var cursor = queued.Timestamp; var runnable = DispatcherEvent.IsRunnable(queued.Data1);
                foreach (var change in row.PriorityChanges)
                {
                    Segment((cursor - queued.Timestamp) * 1000d / _frequency, (change.Timestamp - cursor) * 1000d / _frequency,
                        runnable ? RunnableBrush : ChartPalette.Muted);
                    cursor = change.Timestamp; runnable = DispatcherEvent.IsRunnable(change.Data1);
                }
                var offset = (cursor - queued.Timestamp) * 1000d / _frequency;
                Segment(offset, Math.Max(0, queueMs - offset), runnable ? RunnableBrush : ChartPalette.Muted);
            }
            else Segment(0, queueMs, ChartPalette.Muted); // Loss prevents classifying priority intervals.
            if (row.ExecutionMs is { } execution) Segment(queueMs, execution, ChartPalette.Accent);
            _hits.Add((new Rect(0, y - 2, Bounds.Width, 23), row));
            void Segment(double offset, double duration, IBrush brush)
            {
                if (duration <= 0) return;
                var x = left + offset / _maximum * width;
                context.FillRectangle(brush, new Rect(x, y + 2, Math.Max(.5, duration / _maximum * width), 16));
            }
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        foreach (var hit in _hits)
            if (hit.Bounds.Contains(e.GetPosition(this)))
            { Focus(); Select(hit.Row.Id); OperationSelected?.Invoke(hit.Row); e.Handled = true; break; }
    }
}
