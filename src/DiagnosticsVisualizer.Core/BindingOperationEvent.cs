namespace DiagnosticsVisualizer.Core;

public enum BindingOperationKind
{
    Attach = 1, Start, Stop, Dispose, Publish, Deliver, Complete, WriteSource, UpdateSource,
    UpdateTarget, Convert, ConvertBack, ResourceSubscribe, ResourceUnsubscribe, ResourceInvalidated,
    ThemeChanged, OwnerChanged, ResolveResource, NodeValue, NodeError, ValidationError
}
public enum BindingOperationOutcome { Threw, Returned, True, False }
public enum BindingValueKind { Unknown, Null, Value, Unset, Unchanged, DoNothing }

/// <summary>Observed arguments/state, not a reconstruction of effective target values or subscriptions.</summary>
public static class BindingOperationEvent
{
    private const long Magic = 0x424e440100000000;
    private const long Mask = unchecked((long)0xffffffff00000000);
    public const int FactsMask = 0x01fff800;
    public static int Facts(BindingValueKind value, bool? running = null, int? error = null,
        bool? valueChanged = null, bool? errorChanged = null, bool? produceValue = null)
        => ((int)value << 11) | (Tri(running) << 14) | ((error is null ? 0 : error.Value + 1) << 16) |
           (Tri(valueChanged) << 19) | (Tri(errorChanged) << 21) | (Tri(produceValue) << 23);
    private static int Tri(bool? value) => value is null ? 0 : value.Value ? 2 : 1;
    private static bool? ReadTri(long code, int shift) => ((code >> shift) & 3) switch { 1 => false, 2 => true, _ => null };
    public static BindingValueKind ValueKind(long code) => (BindingValueKind)((code >> 11) & 7);
    public static bool? Running(long code) => ReadTri(code, 14);
    public static int? Error(long code) => ((code >> 16) & 7) is var error && error > 0 ? (int)error - 1 : null;
    public static bool? ValueChanged(long code) => ReadTri(code, 19);
    public static bool? ErrorChanged(long code) => ReadTri(code, 21);
    public static bool? ProduceValue(long code) => ReadTri(code, 23);
    public static bool ValidFacts(int facts) => (facts & ~FactsMask) == 0 && ValueKind(facts) <= BindingValueKind.DoNothing &&
        ((facts >> 14) & 3) < 3 && ((facts >> 16) & 7) <= 3 && ((facts >> 19) & 3) < 3 &&
        ((facts >> 21) & 3) < 3 && ((facts >> 23) & 3) < 3;
    public static long BeginCode(BindingOperationKind kind, int facts) => Magic | 1 | ((long)kind << 4) | (uint)facts;
    public static long ContextCode(BindingOperationKind kind) => Magic | 2 | ((long)kind << 4);
    public static long InputCode(BindingOperationKind kind) => Magic | 3 | ((long)kind << 4);
    public static long EndCode(BindingOperationKind kind, BindingOperationOutcome outcome, int facts)
        => Magic | 4 | ((long)kind << 4) | ((long)outcome << 9) | (uint)facts;
    public static bool IsEvent(in TraceEvent e) => e.Subsystem == Subsystem.Bindings && (e.Data0 & Mask) == Magic;
    public static int Phase(in TraceEvent e) => (int)(e.Data0 & 15);
    public static BindingOperationKind Operation(in TraceEvent e) => (BindingOperationKind)((e.Data0 >> 4) & 31);
    public static BindingOperationOutcome Outcome(in TraceEvent e) => (BindingOperationOutcome)((e.Data0 >> 9) & 3);
    public static bool IsEnd(in TraceEvent e) => IsEvent(e) && Phase(e) == 4;
    public static long OperationId(in TraceEvent e) => IsEnd(e) ? e.Id : e.ParentId;
    public static bool IsValid(in TraceEvent e)
    {
        var kind = Operation(e); var facts = (int)(e.Data0 & FactsMask);
        if (!IsEvent(e) || e.Id <= 0 || e.ObjectId < 0 || e.RelatedId < 0 || (Phase(e) != 3 && e.Data1 < 0) ||
            kind is < BindingOperationKind.Attach or > BindingOperationKind.ValidationError || !ValidFacts(facts)) return false;
        if (IsEnd(e)) return e.Kind == TraceEventKind.Span && e.Duration >= 0 &&
            e.Data0 == EndCode(kind, Outcome(e), facts) &&
            (Outcome(e) is not (BindingOperationOutcome.True or BindingOperationOutcome.False) || kind == BindingOperationKind.WriteSource) && (Outcome(e) != BindingOperationOutcome.Threw || facts == 0 && e.RelatedId == 0);
        return e.Kind == TraceEventKind.Link && e.ParentId > 0 && e.Duration == 0 && (Phase(e) switch
        {
            1 => e.Data0 == BeginCode(kind, facts),
            2 => e.Data0 == ContextCode(kind),
            3 => e.Data0 == InputCode(kind) && e.RelatedId == 0 && (e.Data1 == long.MinValue || e.Data1 is >= int.MinValue and <= int.MaxValue),
            _ => false
        });
    }
}
