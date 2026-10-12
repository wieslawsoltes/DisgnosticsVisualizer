namespace DiagnosticsVisualizer.Core;

public enum ResourceLookupOutcome { Threw = 0, Missing = 1, Found = 2, FoundNull = 3 }

/// <summary>Self-describing resource observations in the existing, independently decodable event ABI.</summary>
public static class ResourceLookupEvent
{
    private const long Magic = 0x5253430100000000;
    private const long Mask = unchecked((long)0xffffffff00000000);
    public const long RequestCode = Magic | 1;
    public const long ThemeCode = Magic | 2;
    public static long CompleteCode(ResourceLookupOutcome outcome) => Magic | 3 | ((long)outcome << 8);
    public static bool IsEvent(in TraceEvent e) => e.Subsystem == Subsystem.Resources && (e.Data0 & Mask) == Magic;
    public static bool IsRequest(in TraceEvent e) => IsEvent(e) && e.Data0 == RequestCode;
    public static bool IsTheme(in TraceEvent e) => IsEvent(e) && e.Data0 == ThemeCode;
    public static bool IsCompletion(in TraceEvent e) => IsEvent(e) && (e.Data0 & 0xff) == 3;
    public static ResourceLookupOutcome Outcome(in TraceEvent e) => (ResourceLookupOutcome)((e.Data0 >> 8) & 0xff);
    public static long LookupId(in TraceEvent e) => IsCompletion(e) ? e.Id : e.ParentId;
    public static bool IsValid(in TraceEvent e) => e.Id > 0 && e.Data1 >= 0 && (IsRequest(e) || IsTheme(e)
        ? e.Kind == TraceEventKind.Link && e.ParentId > 0 && e.Duration == 0
        : IsCompletion(e) && e.Kind == TraceEventKind.Span && e.Duration >= 0 &&
          e.Data0 == CompleteCode(Outcome(e)) && (uint)Outcome(e) <= (uint)ResourceLookupOutcome.FoundNull);
}
