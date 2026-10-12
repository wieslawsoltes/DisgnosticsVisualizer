namespace DiagnosticsVisualizer.Core;

/// <summary>Construction/population observations, not heap allocation counters or proof of object lifetime.</summary>
public static class XamlObjectEvent
{
    public const long ConstructionCode = 0x58414D4C01;
    public const long RootCode = 0x58414D4C02;
    public static bool IsOrigin(in TraceEvent e) => e.Kind == TraceEventKind.Link &&
        e.Subsystem == Subsystem.Xaml && e.Data0 is ConstructionCode or RootCode;
}
