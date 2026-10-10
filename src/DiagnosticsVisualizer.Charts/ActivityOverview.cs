using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;

namespace DiagnosticsVisualizer.Charts;

/// <summary>A bounded, allocation-light instrumented-span overview with a half-open range brush.</summary>
public sealed class ActivityOverview : Control
{
    private ProfileResult? _profile;
    private TraceRange? _selection;
    private double? _drag;
    private double _position;
    private FormattedText? _caption;
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.Parse("#334C9FFF"));
    private static readonly Pen SelectionPen = new(ChartPalette.Accent, 1);
    public event Action<TraceRange?>? RangeSelected;
    public ActivityOverview() { Height = 96; Focusable = true; ClipToBounds = true; }
    public void SetProfile(ProfileResult profile, TraceRange? selection)
    {
        _profile = profile; _selection = selection;
        _caption = ChartPalette.Text("SPAN ACTIVITY  ·  drag to analyze a range  ·  double-click to clear  ·  wall time, not CPU utilization / GPU", 10, ChartPalette.Muted);
        InvalidateVisual();
    }
    public void SetSelection(TraceRange? selection) { _selection = selection; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Alternate, new Rect(Bounds.Size));
        if (_caption is not null) context.DrawText(_caption, new(14, 8));
        if (_profile is not { Activity.Length: > 0 } profile) return;
        var area = new Rect(14, 29, Math.Max(1, Bounds.Width - 28), Math.Max(1, Bounds.Height - 36));
        var maximum = Math.Max(.001, profile.Activity.Max(b => b.CpuMs));
        var width = area.Width / profile.Activity.Length;
        for (var i = 0; i < profile.Activity.Length; i++)
        {
            var bucket = profile.Activity[i];
            var height = bucket.CpuMs / maximum * area.Height;
            context.FillRectangle(ChartPalette.For(DiagnosticsVisualizer.Core.Subsystem.Dispatcher), new(area.X + i * width, area.Bottom - height, Math.Max(1, width - 1), height));
            var peak = bucket.BusiestThreadMs / maximum * area.Height;
            context.FillRectangle(ChartPalette.Accent, new(area.X + i * width, area.Bottom - peak, Math.Max(1, width - 1), Math.Min(2, peak)));
        }
        if (_selection is { } selection)
        {
            var left = area.X + (selection.Start - (double)profile.Range.Start) / profile.Range.Duration * area.Width;
            var right = area.X + (selection.End - (double)profile.Range.Start) / profile.Range.Duration * area.Width;
            context.DrawRectangle(SelectionFill, SelectionPen, new Rect(left, area.Y, Math.Max(1, right - left), area.Height));
        }
        context.DrawLine(ChartPalette.Grid, area.BottomLeft, area.BottomRight);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _profile is null) return;
        if (e.ClickCount == 2) { _selection = null; RangeSelected?.Invoke(null); InvalidateVisual(); e.Handled = true; return; }
        _drag = _position = e.GetPosition(this).X; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null) return;
        _position = e.GetPosition(this).X; _selection = Range(_drag.Value, _position); InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } start) return;
        _position = e.GetPosition(this).X;
        _selection = Math.Abs(start - _position) < 3 ? null : Range(start, _position);
        _drag = null; e.Pointer.Capture(null); RangeSelected?.Invoke(_selection); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = null; }
    private TraceRange? Range(double a, double b)
    {
        if (_profile is not { } profile) return null;
        long Time(double x) => profile.Range.Start + (long)(Math.Clamp((x - 14) / Math.Max(1, Bounds.Width - 28), 0, 1) * profile.Range.Duration);
        var start = Time(Math.Min(a, b)); var end = Time(Math.Max(a, b));
        return end > start ? new(start, end) : null;
    }
}
