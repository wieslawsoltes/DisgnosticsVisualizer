using System.Globalization;

namespace DiagnosticsVisualizer.Core;

public enum PropertyOperationKind { Set = 1, Clear, SetCurrent, Coerce, DirectSetAndRaise, Notify, SetterInstance, ValueEntry }
public enum PropertyOperationOutcome { Threw, Returned, Changed, Unchanged }
public enum PropertyValueKind { Unknown, Null, Reference, Redacted, Boolean, Signed, Unsigned, Float64, Float32 }

/// <summary>No object payloads or strings. Scalar bits are present only when separately opted in.</summary>
public readonly record struct PropertyValue(PropertyValueKind Kind, long Bits = 0, long ObjectId = 0)
{
    public bool IsValid => Kind is >= PropertyValueKind.Unknown and <= PropertyValueKind.Float32 && ObjectId >= 0 &&
        (Kind == PropertyValueKind.Reference ? Bits == 0 : ObjectId == 0) &&
        (Kind is PropertyValueKind.Unknown or PropertyValueKind.Null or PropertyValueKind.Redacted ? Bits == 0 : true) &&
        (Kind != PropertyValueKind.Boolean || Bits is 0 or 1) && (Kind != PropertyValueKind.Float32 || Bits is >= int.MinValue and <= int.MaxValue);
    public override string ToString() => Kind switch
    {
        PropertyValueKind.Null => "null", PropertyValueKind.Reference => ObjectId > 0 ? $"object #{ObjectId}" : "reference (identity unavailable)",
        PropertyValueKind.Redacted => "value (redacted)", PropertyValueKind.Boolean => Bits == 0 ? "false" : "true",
        PropertyValueKind.Signed => Bits.ToString(CultureInfo.InvariantCulture),
        PropertyValueKind.Unsigned => unchecked((ulong)Bits).ToString(CultureInfo.InvariantCulture),
        PropertyValueKind.Float64 => BitConverter.Int64BitsToDouble(Bits).ToString("G17", CultureInfo.InvariantCulture),
        PropertyValueKind.Float32 => BitConverter.Int32BitsToSingle((int)Bits).ToString("G9", CultureInfo.InvariantCulture), _ => "unknown"
    };
}

/// <summary>Independent framing-compatible property descriptors and a synchronous completion span.</summary>
public static class PropertyOperationEvent
{
    private const long Magic = 0x5052500100000000;
    private const long Mask = unchecked((long)0xffffffff00000000);
    public const long UnknownPriority = long.MinValue;
    // Flags: direct=1, attached=2, inherits=4, effective notification=8, base notification=16.
    public static long Code(PropertyOperationKind kind, int phase, int flags = 0, PropertyValueKind value = 0, PropertyOperationOutcome outcome = 0)
        => Magic | (long)kind << 4 | (uint)phase | (long)flags << 8 | (long)value << 13 | (long)outcome << 17;
    public static bool IsEvent(in TraceEvent e) => e.Subsystem == Subsystem.Properties && (e.Data0 & Mask) == Magic;
    public static int Phase(in TraceEvent e) => (int)(e.Data0 & 15);
    public static PropertyOperationKind Operation(in TraceEvent e) => (PropertyOperationKind)((e.Data0 >> 4) & 15);
    public static int Flags(in TraceEvent e) => (int)((e.Data0 >> 8) & 31);
    public static PropertyOperationOutcome Outcome(in TraceEvent e) => (PropertyOperationOutcome)((e.Data0 >> 17) & 3);
    public static PropertyValue Value(in TraceEvent e) => new((PropertyValueKind)((e.Data0 >> 13) & 15), e.Data1, e.ObjectId);
    public static bool IsEnd(in TraceEvent e) => IsEvent(e) && Phase(e) == 5;
    public static long OperationId(in TraceEvent e) => IsEnd(e) ? e.Id : e.ParentId;
    public static bool ValidFlags(PropertyOperationKind kind, int flags) => (flags & ~31) == 0 &&
        (kind == PropertyOperationKind.Notify ? (flags & 24) is 8 or 16 : (flags & 24) == 0);
    public static bool IsValid(in TraceEvent e)
    {
        var kind = Operation(e); var phase = Phase(e); var flags = Flags(e);
        if (!IsEvent(e) || kind is < PropertyOperationKind.Set or > PropertyOperationKind.ValueEntry || e.Id <= 0 || e.ObjectId < 0 || e.RelatedId < 0 || e.ParentId < 0) return false;
        if (phase == 5) return ValidFlags(kind, flags) && e.Kind == TraceEventKind.Span && e.Duration >= 0 && e.Data1 >= 0 && e.Data0 == Code(kind, 5, flags, outcome: Outcome(e)) &&
            (Outcome(e) is not (PropertyOperationOutcome.Changed or PropertyOperationOutcome.Unchanged) || kind == PropertyOperationKind.DirectSetAndRaise);
        if (e.Kind != TraceEventKind.Link || e.ParentId <= 0 || e.Duration != 0) return false;
        return phase switch
        {
            1 => e.Data1 >= 0 && e.Data0 == Code(kind, 1, flags) && ValidFlags(kind, flags),
            2 => e.Data0 == Code(kind, 2) && e.RelatedId == 0 && (e.Data1 == UnknownPriority || e.Data1 is >= int.MinValue and <= int.MaxValue),
            3 or 4 => e.Data0 == Code(kind, phase, value: Value(e).Kind) && e.RelatedId == 0 && Value(e).IsValid,
            _ => false
        };
    }
    public static string PriorityName(long priority) => priority switch
    { -1 => "Animation", 0 => "LocalValue", 1 => "StyleTrigger", 2 => "Template", 3 => "Style", 4 => "Inherited", int.MaxValue => "Unset/default", UnknownPriority => "Unknown", _ => priority.ToString(CultureInfo.InvariantCulture) };
}
