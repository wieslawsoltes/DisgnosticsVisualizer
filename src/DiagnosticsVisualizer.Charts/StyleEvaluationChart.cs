using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

/// <summary>Original selector and frame calls on one elapsed-time scale. No inferred matches or setter winners.</summary>
public sealed class StyleEvaluationChart : Control
{
    private static readonly IBrush Conditional = new SolidColorBrush(Color.Parse("#F0BD65"));
    private static readonly IBrush Positive = new SolidColorBrush(Color.Parse("#70D8BA"));
    private static readonly IBrush Failure = new SolidColorBrush(Color.Parse("#EC8DA4"));
    private const int Limit = 96;
    private (StyleEvaluation Call, int Depth)[] _rows = [];
    private FormattedText[] _labels = [];
    private FormattedText _caption = ChartPalette.Text("Select a styling evaluation", 10, ChartPalette.Muted);
    private double _start, _duration = 1;
    private long _selected;
    public event Action<StyleEvaluation>? EvaluationSelected;
    public StyleEvaluationChart() { Height = 48; VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; Focusable = true; ClipToBounds = true; AutomationProperties.SetName(this, "Styling evaluation cascade"); }
    public void SetEvaluation(StyleEvaluation? root, (StyleEvaluation Call, int Depth)[] rows, TraceView? trace)
    {
        _rows = rows.Take(Limit).ToArray();
        _start = root?.Timestamp ?? 0;
        _duration = Math.Max(1, (root?.Completion?.End ?? rows.Select(r => r.Call.Completion?.End ?? r.Call.Timestamp).DefaultIfEmpty((long)_start).Max()) - _start);
        _labels = _rows.Select(r => ChartPalette.Text(new string(' ', Math.Min(r.Depth, 12) * 2) + $"{r.Call.Kind} #{r.Call.SubjectId} · {r.Call.Status}", 10)).ToArray();
        _caption = ChartPalette.Text(root is null ? "Select a styling evaluation" :
            $"{_rows.Length:N0}/{rows.Length:N0} calls · 0–{_duration * 1000 / (trace?.Frequency ?? 1):N3} ms · green: active/always · amber: conditional · gray: inactive/never", 10, ChartPalette.Muted);
        Height = Math.Max(48, 30 + _rows.Length * 24); InvalidateVisual();
    }
    public void Select(long id) { _selected = id; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        using (context.PushClip(new Rect(8, 0, Math.Max(0, Bounds.Width - 16), 28))) context.DrawText(_caption, new Point(10, 8));
        var left = Math.Clamp(Bounds.Width * .52, 160, 420); var width = Math.Max(1, Bounds.Width - left - 12);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i].Call; var y = 30 + i * 24;
            if (row.Id == _selected) context.FillRectangle(ChartPalette.Selection, new Rect(0, y, Bounds.Width, 23));
            else if (i % 2 == 0) context.FillRectangle(ChartPalette.Alternate, new Rect(0, y, Bounds.Width, 23));
            using (context.PushClip(new Rect(8, y, Math.Max(0, left - 16), 23))) context.DrawText(_labels[i], new Point(10, y + 4));
            if (row.DurationMs is null || row.Completion is not { } span) continue;
            var x = left + Math.Clamp((span.Timestamp - _start) / _duration, 0, 1) * width;
            var size = Math.Clamp(span.Duration / _duration, 0, 1) * width;
            var brush = row.Status == "Threw" ? Failure : (row.Status == "Active" || row.Status.StartsWith("Always", StringComparison.Ordinal)) ? Positive : row.Status.StartsWith("Conditional", StringComparison.Ordinal) ? Conditional : ChartPalette.Muted;
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
        base.OnKeyDown(e); if (e.Key is not (Key.Up or Key.Down) || _rows.Length == 0) return;
        var index = Array.FindIndex(_rows, r => r.Call.Id == _selected);
        Choose(Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, _rows.Length - 1)); e.Handled = true;
    }
    private void Choose(int index) { Select(_rows[index].Call.Id); EvaluationSelected?.Invoke(_rows[index].Call); }
}
