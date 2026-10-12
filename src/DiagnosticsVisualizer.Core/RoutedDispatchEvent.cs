namespace DiagnosticsVisualizer.Core;

public enum RoutedStepKind { Begin = 1, Dispatch = 2, ClassListeners = 3, Handler = 4, SkippedHandled = 5, SkippedRoute = 6 }

/// <summary>Versioned semantic payload in existing event fields; old readers still understand the span/instant.</summary>
public static class RoutedDispatchEvent
{
    private const long Magic = 0x5254450100000000;
    private const long Mask = unchecked((long)0xffffffff00000000);
    private const long Allowed = 0xff | 0x700 | 0xf000;
    public static long Pack(RoutedStepKind kind, int route, bool before, bool after, bool handledEventsToo, bool returned) =>
        Magic | (byte)kind | ((long)(route & 7) << 8) | (before ? 0x1000L : 0) | (after ? 0x2000L : 0) |
        (handledEventsToo ? 0x4000L : 0) | (returned ? 0x8000L : 0);
    public static bool IsEvent(in TraceEvent e) => e.Subsystem == Subsystem.RoutedEvents && (e.Data0 & Mask) == Magic;
    public static RoutedStepKind Kind(in TraceEvent e) => (RoutedStepKind)(e.Data0 & 0xff);
    public static int Route(in TraceEvent e) => (int)((e.Data0 >> 8) & 7);
    public static bool HandledBefore(in TraceEvent e) => (e.Data0 & 0x1000) != 0;
    public static bool HandledAfter(in TraceEvent e) => (e.Data0 & 0x2000) != 0;
    public static bool HandledEventsToo(in TraceEvent e) => (e.Data0 & 0x4000) != 0;
    public static bool Returned(in TraceEvent e) => (e.Data0 & 0x8000) != 0;
    public static bool IsValid(in TraceEvent e)
    {
        if (!IsEvent(e) || e.Data1 <= 0 || (e.Data0 & ~(Mask | Allowed)) != 0) return false;
        return Kind(e) switch
        {
            RoutedStepKind.Begin => e.Kind == TraceEventKind.Link && e.Duration == 0,
            RoutedStepKind.Dispatch => e.Kind == TraceEventKind.Span && e.Id == e.Data1,
            RoutedStepKind.ClassListeners or RoutedStepKind.Handler => e.Kind == TraceEventKind.Span && Route(e) is 1 or 2 or 4,
            RoutedStepKind.SkippedHandled or RoutedStepKind.SkippedRoute => e.Kind == TraceEventKind.Instant && e.Duration == 0 && Route(e) is 1 or 2 or 4,
            _ => false
        };
    }
    public static string RouteName(int route) => route switch
    {
        1 => "Direct", 2 => "Tunnel", 4 => "Bubble", 6 => "Tunnel + Bubble", _ => $"Routes {route}"
    };
}
