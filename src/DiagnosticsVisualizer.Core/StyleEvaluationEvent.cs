namespace DiagnosticsVisualizer.Core;

public enum StyleEvaluationKind { Match = 1, Evaluate = 2, Attach = 3, Frame = 4, Signal = 5 }
public enum StyleEvaluationOutcome
{
    Threw = 0, NeverThisType = 1, NeverThisInstance = 2, Conditional = 3,
    AlwaysThisInstance = 4, AlwaysThisType = 5, Inactive = 6, Active = 7, Delivered = 8
}

/// <summary>Original selector results and frame observations. Conditional is not an active/winning setter.</summary>
public static class StyleEvaluationEvent
{
    private const long Magic = 0x5354590100000000;
    private const long Mask = unchecked((long)0xffffffff00000000);
    public static long BeginCode(StyleEvaluationKind kind, bool input) => Magic | 1 | ((long)kind << 8) | (input ? 1L << 24 : 0);
    public static long ContextCode(StyleEvaluationKind kind) => Magic | 2 | ((long)kind << 8);
    public static long EndCode(StyleEvaluationKind kind, StyleEvaluationOutcome outcome, bool changed = false)
        => Magic | 3 | ((long)kind << 8) | ((long)outcome << 16) | (changed ? 1L << 25 : 0);
    public static bool IsEvent(in TraceEvent e) => e.Subsystem is Subsystem.Styles or Subsystem.Selectors && (e.Data0 & Mask) == Magic;
    public static bool IsBegin(in TraceEvent e) => IsEvent(e) && (e.Data0 & 255) == 1;
    public static bool IsContext(in TraceEvent e) => IsEvent(e) && (e.Data0 & 255) == 2;
    public static bool IsEnd(in TraceEvent e) => IsEvent(e) && (e.Data0 & 255) == 3;
    public static StyleEvaluationKind Operation(in TraceEvent e) => (StyleEvaluationKind)((e.Data0 >> 8) & 255);
    public static StyleEvaluationOutcome Outcome(in TraceEvent e) => (StyleEvaluationOutcome)((e.Data0 >> 16) & 255);
    public static bool Input(in TraceEvent e) => (e.Data0 & (1L << 24)) != 0;
    public static bool Changed(in TraceEvent e) => (e.Data0 & (1L << 25)) != 0;
    public static long EvaluationId(in TraceEvent e) => IsEnd(e) ? e.Id : e.ParentId;
    public static Subsystem Category(StyleEvaluationKind kind) => kind is StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate ? Subsystem.Selectors : Subsystem.Styles;
    public static bool IsValid(in TraceEvent e)
    {
        var kind = Operation(e); var outcome = Outcome(e);
        if (!IsEvent(e) || e.Id <= 0 || e.ObjectId < 0 || e.RelatedId < 0 || e.Data1 < 0 ||
            kind is < StyleEvaluationKind.Match or > StyleEvaluationKind.Signal || e.Subsystem != Category(kind)) return false;
        if (IsBegin(e)) return e.Kind == TraceEventKind.Link && e.ParentId > 0 && e.Duration == 0 && e.Data0 == BeginCode(kind, Input(e)) &&
            (!Input(e) || kind is StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate or StyleEvaluationKind.Signal);
        if (IsContext(e)) return e.Kind == TraceEventKind.Link && e.ParentId > 0 && e.Duration == 0 && e.RelatedId == 0 && e.Data0 == ContextCode(kind);
        if (!IsEnd(e) || e.Kind != TraceEventKind.Span || e.Duration < 0 || e.Data0 != EndCode(kind, outcome, Changed(e)) ||
            (Changed(e) && (kind != StyleEvaluationKind.Frame || outcome == StyleEvaluationOutcome.Threw))) return false;
        return outcome == StyleEvaluationOutcome.Threw || (kind switch
        {
            StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate or StyleEvaluationKind.Attach => outcome is >= StyleEvaluationOutcome.NeverThisType and <= StyleEvaluationOutcome.AlwaysThisType,
            StyleEvaluationKind.Frame => outcome is StyleEvaluationOutcome.Active or StyleEvaluationOutcome.Inactive,
            StyleEvaluationKind.Signal => outcome == StyleEvaluationOutcome.Delivered,
            _ => false
        });
    }
}
