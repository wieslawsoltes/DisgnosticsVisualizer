using System.Text;
using System.Text.Json;

namespace DiagnosticsVisualizer.Core;

public abstract record NativeMetadata
{
    public abstract int Kind { get; }
    public virtual int Key => 0;
}
public sealed record NativeProcessInfo(int ProcessId, int PointerBytes, int FormatVersion = 2) : NativeMetadata { public override int Kind => 1; }
public sealed record NativeThreadInfo(int Slot, ulong OsThreadId) : NativeMetadata { public override int Kind => 2; public override int Key => Slot; }
public sealed record NativeMethodInfo(int SiteId, int Status, int HResult, long Compilations) : NativeMetadata { public override int Kind => 3; public override int Key => SiteId; }
public sealed record NativeCaptureEnd(bool HasFooter, bool TruncatedTail, int Reason, long EndTimestamp,
    long DroppedRecords, long RecorderFaults, long IoFaults, long RequestedMethods, long InstrumentedMethods,
    long RejitCompilations, long RejitErrors, long OpenFrames, long Records, long Bytes) : NativeMetadata { public override int Kind => 4; }

/// <summary>Bounded metadata-only provenance survives ordinary binary export; no target event is fabricated.</summary>
public sealed record NativeControlState(int Revision, int State, long Timestamp, int Operation, int HResult, int Methods, int FailedMethods) : NativeMetadata
{
    public override int Kind => 5;
    public string StateText => State switch { 0 => "Recording", 1 => "Paused (new roots)", 2 => "Stop requested", 3 => "Revert accepted", 4 => "Revert incomplete", _ => "Unknown" };
}

public static class NativeMetadataSymbol
{
    public const string Prefix = "\u001fAVNATIVE1:";
    public const int MaximumEntries = 4163; // header + 64 threads + 4096 method statuses + footer + control state
    public static string Encode(NativeMetadata metadata)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray(); json.WriteNumberValue(metadata.Kind);
            switch (metadata)
            {
                case NativeProcessInfo p: json.WriteNumberValue(p.ProcessId); json.WriteNumberValue(p.PointerBytes); json.WriteNumberValue(p.FormatVersion); break;
                case NativeThreadInfo t: json.WriteNumberValue(t.Slot); json.WriteNumberValue(t.OsThreadId); break;
                case NativeMethodInfo m: json.WriteNumberValue(m.SiteId); json.WriteNumberValue(m.Status); json.WriteNumberValue(m.HResult); json.WriteNumberValue(m.Compilations); break;
                case NativeControlState c:
                    json.WriteNumberValue(c.Revision); json.WriteNumberValue(c.State); json.WriteNumberValue(c.Timestamp); json.WriteNumberValue(c.Operation);
                    json.WriteNumberValue(c.HResult); json.WriteNumberValue(c.Methods); json.WriteNumberValue(c.FailedMethods); break;
                case NativeCaptureEnd e:
                    json.WriteBooleanValue(e.HasFooter); json.WriteBooleanValue(e.TruncatedTail); json.WriteNumberValue(e.Reason);
                    foreach (var value in new[] { e.EndTimestamp, e.DroppedRecords, e.RecorderFaults, e.IoFaults, e.RequestedMethods, e.InstrumentedMethods, e.RejitCompilations, e.RejitErrors, e.OpenFrames, e.Records, e.Bytes }) json.WriteNumberValue(value);
                    break;
                default: throw new InvalidDataException("Unsupported native metadata.");
            }
            json.WriteEndArray();
        }
        var encoded = Prefix + Encoding.UTF8.GetString(buffer.ToArray());
        if (!TryDecode(encoded, out _)) throw new InvalidDataException("Invalid native capture metadata.");
        return encoded;
    }
    public static bool TryDecode(string text, out NativeMetadata? value)
    {
        value = null;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length > 2048) return false;
        try
        {
            using var doc = JsonDocument.Parse(text.AsMemory(Prefix.Length), new JsonDocumentOptions { MaxDepth = 2 });
            var a = doc.RootElement;
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 1) return false;
            switch (a[0].GetInt32())
            {
                case 1 when a.GetArrayLength() is 3 or 4:
                    var process = new NativeProcessInfo(a[1].GetInt32(), a[2].GetInt32(), a.GetArrayLength() == 4 ? a[3].GetInt32() : 1);
                    if (process.ProcessId <= 0 || process.PointerBytes != 8 || process.FormatVersion is < 1 or > 4) return false; value = process; return true;
                case 2 when a.GetArrayLength() == 3:
                    var thread = new NativeThreadInfo(a[1].GetInt32(), a[2].GetUInt64());
                    if (thread.Slot is < 1 or > 64 || thread.OsThreadId == 0) return false; value = thread; return true;
                case 3 when a.GetArrayLength() == 5:
                    var method = new NativeMethodInfo(a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32(), a[4].GetInt64());
                    if (method.SiteId is < 1 or > 4096 || method.Status is < -8 or > 3 || method.Compilations < 0 || method.Status == 3 && method.HResult != 0 || method.Status == -8 && method.HResult == 0) return false; value = method; return true;
                case 4 when a.GetArrayLength() == 15:
                    var e = new NativeCaptureEnd(a[1].GetBoolean(), a[2].GetBoolean(), a[3].GetInt32(), a[4].GetInt64(), a[5].GetInt64(), a[6].GetInt64(),
                        a[7].GetInt64(), a[8].GetInt64(), a[9].GetInt64(), a[10].GetInt64(), a[11].GetInt64(), a[12].GetInt64(), a[13].GetInt64(), a[14].GetInt64());
                    if (e.Reason is < 0 or > 7 || e.HasFooter && e.Reason == 4 || !e.HasFooter && e.Reason != 4 || e.HasFooter && e.TruncatedTail) return false;
                    for (var n = 4; n < 15; n++) if (a[n].GetInt64() < 0) return false;
                    if (e.Bytes > 128L * 1024 * 1024 || e.Records > 3_000_000) return false;
                    value = e; return true;
                case 5 when a.GetArrayLength() == 8:
                    var c = new NativeControlState(a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt64(), a[4].GetInt32(), a[5].GetInt32(), a[6].GetInt32(), a[7].GetInt32());
                    if (c.Revision is < 1 or > 1024 || c.State is < 0 or > 4 || c.Timestamp < 0 || c.Operation is < 0 or > 4 ||
                        c.Methods is < 0 or > 4096 || c.FailedMethods < 0 || c.FailedMethods > c.Methods ||
                        c.Operation == 0 && (c.Revision != 1 || c.State != 0) || c.Operation == 1 && c.State != 1 ||
                        c.Operation == 2 && c.State != 0 || c.Operation == 3 && c.State != 2 || c.Operation == 4 && c.State is not (3 or 4) ||
                        c.Operation != 4 && (c.HResult != 0 || c.Methods != 0 || c.FailedMethods != 0) ||
                        c.State == 3 && (c.HResult != 0 || c.FailedMethods != 0) || c.State == 4 && c.HResult >= 0) return false;
                    value = c; return true;
                default: return false;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { return false; }
    }
}

public static class NativeMethodEvent
{
    public const long BeginCode = 0x4e41544d00000001, ReturnedCode = 0x4e41544d00000002, UnwoundCode = 0x4e41544d00000003;
    public static bool IsEvent(in TraceEvent e) => e.Data0 is BeginCode or ReturnedCode or UnwoundCode;
    public static bool IsValid(in TraceEvent e) => IsEvent(e) && e.Subsystem == Subsystem.Application && e.ObjectId == 0 && e.RelatedId == 0 &&
        e.ThreadId is >= 1 and <= 64 && e.NameId is >= 1 and <= 4096 && e.Timestamp >= 0 && e.Duration >= 0 && e.Timestamp <= long.MaxValue - e.Duration &&
        e.Data1 > 0 && (e.Data1 >> 48) == e.ThreadId && (e.Data1 & 0xffffffffffff) != 0 &&
        e.Id == e.Data1 * 2 + (e.Data0 == BeginCode ? 0 : 1) &&
        (e.ParentId == 0 || (e.ParentId & 1) == 1 && e.ParentId < e.Id && (e.ParentId >> 49) == e.ThreadId) &&
        (e.Data0 == BeginCode ? e.Kind == TraceEventKind.Link && e.Duration == 0 : e.Kind == TraceEventKind.Span);
    public static string Outcome(in TraceEvent e) => e.Data0 switch { BeginCode => "Entered", ReturnedCode => "Returned", UnwoundCode => "Unwound", _ => "Unknown" };
}
