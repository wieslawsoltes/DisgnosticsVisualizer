using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Analysis;

namespace DiagnosticsVisualizer.Charts;

public sealed class CostChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<CostRow>?> RowsProperty = AvaloniaProperty.Register<CostChart, IReadOnlyList<CostRow>?>(nameof(Rows));
    public IReadOnlyList<CostRow>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public event Action<CostRow>? CostSelected;
    static CostChart() { AffectsRender<CostChart>(RowsProperty); }
    public CostChart() { MinHeight = 240; ClipToBounds = true; }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        var rows = Rows?.Take(Math.Max(1, (int)((Bounds.Height - 46) / 42))).ToArray() ?? [];
        ChartPalette.Label(context, "SELF TIME  /  EXCLUDES NESTED CHILD SPANS", new(18, 14), 10, ChartPalette.Muted);
        if (rows.Length == 0) { ChartPalette.Label(context, "No recorded span costs.", new(18, 60), 13); return; }
        var max = Math.Max(.001, rows.Max(row => row.SelfMs)); var width = Math.Max(1, Bounds.Width - 44);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var y = 44 + i * 42;
            ChartPalette.Label(context, ChartPalette.ShortName(row.Name), new(18, y), 11);
            var value = ChartPalette.Text($"{row.SelfMs:F2} ms  ·  {row.Count:N0}×", 10, ChartPalette.Muted);
            context.DrawText(value, new(Math.Max(18, Bounds.Width - value.Width - 18), y));
            context.DrawRectangle(ChartPalette.Alternate, null, new(18, y + 19, width, 7), 3, 3);
            context.DrawRectangle(ChartPalette.For(row.Subsystem), null, new(18, y + 19, width * row.SelfMs / max, 7), 3, 3);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var index = (int)((e.GetPosition(this).Y - 44) / 42);
        if (e.GetPosition(this).Y >= 44 && Rows is { } rows && index >= 0 && index < rows.Count) CostSelected?.Invoke(rows[index]);
    }
}
