using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed class LayoutBoxControl : Control
{
    public static readonly StyledProperty<NodeSnapshot?> NodeProperty = AvaloniaProperty.Register<LayoutBoxControl, NodeSnapshot?>(nameof(Node));
    public NodeSnapshot? Node { get => GetValue(NodeProperty); set => SetValue(NodeProperty, value); }
    static LayoutBoxControl() { AffectsRender<LayoutBoxControl>(NodeProperty); }
    public LayoutBoxControl() { MinHeight = 260; ClipToBounds = true; }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ChartPalette.Background, new Rect(Bounds.Size));
        ChartPalette.Label(context, "LAYOUT BOX  ·  DEVICE-INDEPENDENT PIXELS", new(14, 14), 10, ChartPalette.Muted);
        if (Node is not { } node) { ChartPalette.Label(context, "Select a control in either tree.", new(20, 70), 12); return; }
        var outer = new Rect(22, 54, Math.Max(30, Bounds.Width - 44), Math.Max(80, Math.Min(200, Bounds.Height - 120)));
        var border = outer.Deflate(15); var padding = border.Deflate(14); var content = padding.Deflate(14);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#32302A")), new Pen(new SolidColorBrush(Color.Parse("#92764C")), 1), outer, 6, 6);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#334036")), new Pen(new SolidColorBrush(Color.Parse("#68886A")), 1), border, 4, 4);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#293F48")), new Pen(new SolidColorBrush(Color.Parse("#568D9B")), 1), padding, 3, 3);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#263C61")), new Pen(ChartPalette.Accent, 1), content, 2, 2);
        ChartPalette.Label(context, "parent space", outer.TopLeft + new Vector(7, 2), 9, ChartPalette.Muted);
        var size = ChartPalette.Text($"{node.Width:0.##} × {node.Height:0.##}", 16);
        context.DrawText(size, new(content.Center.X - size.Width / 2, content.Center.Y - size.Height / 2));
        ChartPalette.Label(context, $"Position  {node.X:0.##}, {node.Y:0.##}", new(22, outer.Bottom + 18), 11);
        ChartPalette.Label(context, $"Visual #{node.VisualParent}   Logical #{node.LogicalParent}   Template #{node.TemplatedParent}", new(22, outer.Bottom + 42), 10, ChartPalette.Muted);
        ChartPalette.Label(context, "Schematic boxes; dimensions describe the selected Bounds, not inferred CSS margins.", new(22, outer.Bottom + 64), 9, ChartPalette.Muted);
    }
}
