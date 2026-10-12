using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Bounded, common-scale synchronous handler timeline. The companion table retains every observed step.</summary>
public sealed class RoutedRouteChart : Control
{
    private const int Limit = 96;
    private static readonly Pen HandledPen = new(ChartPalette.Foreground, 1);
    private RoutedStep[] _steps = [];
    private FormattedText[] _labels = [];
    private FormattedText _caption = ChartPalette.Text("Select a routed dispatch", 10, ChartPalette.Muted);
    private long _start, _duration = 1, _selected;
    public event Action<RoutedStep>? StepSelected;
    public RoutedRouteChart() { Focusable = true; ClipToBounds = true; Height = 48; AutomationProperties.SetName(this, "Routed event propagation timeline"); }
    public void SetDispatch(RoutedDispatch? dispatch, RoutedStep[] steps, TraceView? trace)
    {
        _steps = dispatch?.Status == "Conflicting / invalid" ? [] : steps.Take(Limit).ToArray();
        _labels = _steps.Select(s => ChartPalette.Text($"{s.Phase} · #{s.ObjectId} · {s.Kind} · {s.HandledBefore} -> {s.HandledAfter}", 10)).ToArray();
        _start = dispatch?.Start ?? 0;
        _duration = Math.Max(1, (dispatch?.Completion?.End ?? _steps.Select(s => s.Event.End).DefaultIfEmpty(_start).Max()) - _start);
        _caption = ChartPalette.Text(dispatch is null ? "Select a routed dispatch" :
            $"{_steps.Length:N0} / {steps.Length:N0} displayed observations · 0–{trace?.Milliseconds(_duration):N3} ms · gray ticks: skipped · outlines: handled changed", 10, ChartPalette.Muted);
        Height = Math.Max(48, 32 + 24 * _steps.Length); InvalidateVisual();
    }
    public void Select(long eventId) { _selected = eventId; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        using (context.PushClip(new Rect(8, 0, Math.Max(0, Bounds.Width - 16), 28))) context.DrawText(_caption, new Point(10, 8));
        var left = Math.Min(400, Math.Max(150, Bounds.Width * .50)); var width = Math.Max(1, Bounds.Width - left - 12);
        for (var i = 0; i < _steps.Length; i++)
        {
            var step = _steps[i]; var y = 30 + i * 24;
            if (step.EventId == _selected) context.FillRectangle(ChartPalette.Selection, new Rect(0, y, Bounds.Width, 23));
            else if (i % 2 == 0) context.FillRectangle(ChartPalette.Alternate, new Rect(0, y, Bounds.Width, 23));
            using (context.PushClip(new Rect(8, y, Math.Max(0, left - 16), 23))) context.DrawText(_labels[i], new Point(10, y + 4));
            var x = left + Math.Clamp((step.Event.Timestamp - _start) / (double)_duration, 0, 1) * width;
            var w = Math.Clamp(step.Event.Duration / (double)_duration, 0, 1) * width;
            var brush = step.Event.Kind != TraceEventKind.Span ? ChartPalette.Muted : step.Status == "Threw" ? ChartPalette.For(Subsystem.RoutedEvents) :
                step.Phase == "Tunnel" ? ChartPalette.For(Subsystem.Styles) : ChartPalette.Accent;
            context.FillRectangle(brush, new Rect(x, y + 5, Math.Max(2, w), 13));
            if (step.ChangedHandled) context.DrawRectangle(HandledPen, new Rect(x - 2, y + 3, Math.Max(2, w) + 4, 17));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var index = (int)Math.Floor((e.GetPosition(this).Y - 30) / 24);
        if (index < 0 || index >= _steps.Length) return;
        Focus(); Select(_steps[index].EventId); StepSelected?.Invoke(_steps[index]); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Up or Key.Down) || _steps.Length == 0) return;
        var index = Array.FindIndex(_steps, s => s.EventId == _selected);
        index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, _steps.Length - 1);
        Select(_steps[index].EventId); StepSelected?.Invoke(_steps[index]); e.Handled = true;
    }
}
