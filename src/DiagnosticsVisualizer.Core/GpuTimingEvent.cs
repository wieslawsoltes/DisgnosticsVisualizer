using System.Text;
using System.Text.Json;

namespace DiagnosticsVisualizer.Core;

public enum GpuQueryOutcome
{
    Available, Unsupported, CapacityExceeded, SubmissionAborted, PendingLimit,
    ContextLost, ContextDisposed, BackendFault, InvalidClock
}

/// <summary>Device-clock provenance. GPU counters are nanoseconds in an uncalibrated context clock, not CPU ticks.</summary>
public sealed record GpuTimingSite(string Name, string Api, string Vendor, string Renderer, int CounterBits, string Limitation = "");

public static class GpuTimingSymbol
{
    public const string Prefix = "\u001fAVGPU1:";
    public static string Encode(GpuTimingSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var text in new[] { site.Name, site.Api, site.Vendor, site.Renderer }) writer.WriteStringValue(text);
            writer.WriteNumberValue(site.CounterBits); writer.WriteStringValue(site.Limitation); writer.WriteEndArray();
        }
        var value = Prefix + Encoding.UTF8.GetString(stream.ToArray());
        if (!TryDecode(value, out _)) throw new ArgumentException("Invalid GPU timing descriptor.", nameof(site));
        return value;
    }
    public static bool TryDecode(string text, out GpuTimingSite? site)
    {
        site = null;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length > 4096) return false;
        try
        {
            using var doc = JsonDocument.Parse(text.AsMemory(Prefix.Length), new JsonDocumentOptions { MaxDepth = 2 });
            var a = doc.RootElement;
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 6) return false;
            var strings = new string[5]; var indices = new[] { 0, 1, 2, 3, 5 };
            for (var i = 0; i < indices.Length; i++)
            {
                if (a[indices[i]].ValueKind != JsonValueKind.String || a[indices[i]].GetString() is not { } s || s.Length > 512 || s.Any(char.IsControl)) return false;
                strings[i] = s;
            }
            var bits = a[4].GetInt32();
            if (strings[0].Length == 0 || strings[1].Length == 0 || bits is < 0 or > 64) return false;
            site = new(strings[0], strings[1], strings[2], strings[3], bits, strings[4]); return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException) { return false; }
    }
}

public static class GpuTimingEvent
{
    public const long Begin = 0x4750555400000001, Submitted = 0x4750555400000002,
        CounterStart = 0x4750555400000003, CounterEnd = 0x4750555400000004, Completed = 0x4750555400000005;
    public static bool IsMarker(in TraceEvent e) => e.Data0 is Begin or Submitted or CounterStart or CounterEnd or Completed;
    public static bool IsValid(in TraceEvent e) => IsMarker(e) && e.Subsystem == Subsystem.Rendering &&
        e.Kind == TraceEventKind.Link && e.Duration == 0 && e.Timestamp >= 0 && e.Id > 0 && e.ObjectId > 0 &&
        e.RelatedId == 0 && e.NameId > 0 && e.ThreadId > 0 &&
        (e.Data0 == Begin || e.ParentId > 0) &&
        (e.Data0 is not (Begin or Submitted) || e.Data1 == 0) &&
        (e.Data0 != Completed || e.Data1 is >= 0 and <= (long)GpuQueryOutcome.InvalidClock);
}
