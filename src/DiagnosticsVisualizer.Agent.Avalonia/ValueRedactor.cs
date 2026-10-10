using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>Never invokes application-defined ToString methods or reflects arbitrary CLR getters.</summary>
public sealed class ValueRedactor(AgentOptions options)
{
    private static readonly HashSet<string> SafeProperties = new(StringComparer.Ordinal)
    {
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Bounds", "DesiredSize",
        "Margin", "Padding", "BorderThickness", "CornerRadius", "Opacity", "IsVisible", "IsEnabled",
        "IsHitTestVisible", "ClipToBounds", "HorizontalAlignment", "VerticalAlignment", "FontSize",
        "FontWeight", "FontStyle", "FlowDirection", "UseLayoutRounding", "ZIndex", "IsMeasureValid",
        "IsArrangeValid", "RenderTransformOrigin", "Orientation", "Spacing"
    };
    public string Format(string propertyName, object? value)
    {
        if (value is null) return "null";
        if (Sensitive(propertyName)) return "<redacted>";
        if (value is string text) return options.RevealTextValues ? Limit(text, 1024) : "<text redacted>";
        var allowed = options.RevealPrimitiveValues || SafeProperties.Contains(propertyName);
        if (!allowed) return $"<{value.GetType().Name}>";
        return value switch
        {
            bool b => b ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            Enum e => Limit(e.ToString(), 256),
            Size s => $"{s.Width.ToString("G", CultureInfo.InvariantCulture)} × {s.Height.ToString("G", CultureInfo.InvariantCulture)}",
            Rect r => FormattableString.Invariant($"{r.X:G}, {r.Y:G}, {r.Width:G}, {r.Height:G}"),
            Point p => FormattableString.Invariant($"{p.X:G}, {p.Y:G}"),
            Thickness t => FormattableString.Invariant($"{t.Left:G}, {t.Top:G}, {t.Right:G}, {t.Bottom:G}"),
            CornerRadius c => FormattableString.Invariant($"{c.TopLeft:G}, {c.TopRight:G}, {c.BottomRight:G}, {c.BottomLeft:G}"),
            Color c => c.ToString(),
            _ => $"<{value.GetType().FullName}>"
        };
    }
    public static string Limit(string value, int count = 2048) => value.Length <= count ? value : value[..count] + "…";
    private static bool Sensitive(string name) => new[] { "password", "secret", "token", "credential", "connectionstring", "authorization" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
}
