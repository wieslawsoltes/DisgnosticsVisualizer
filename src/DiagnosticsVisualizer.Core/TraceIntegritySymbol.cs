using System.Globalization;

namespace DiagnosticsVisualizer.Core;

public readonly record struct TraceIntegrity(long MissingBlocks, long EvictedEvents);

/// <summary>
/// Loss provenance for rebatching/export, carried in a metadata-only version-1 frame.
/// Older readers can read the file but do not interpret this unused symbol. No event is synthesized.
/// </summary>
public static class TraceIntegritySymbol
{
    public const string Prefix = "\u001fAVLOSS1:";
    public static string Encode(TraceIntegrity integrity)
    {
        if (integrity.MissingBlocks < 0 || integrity.EvictedEvents < 0) throw new ArgumentOutOfRangeException(nameof(integrity));
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{integrity.MissingBlocks}:{integrity.EvictedEvents}");
    }
    public static bool TryDecode(string symbol, out TraceIntegrity integrity)
    {
        integrity = default;
        if (!symbol.StartsWith(Prefix, StringComparison.Ordinal) || symbol.Length > Prefix.Length + 39) return false;
        var payload = symbol.AsSpan(Prefix.Length); var split = payload.IndexOf(':');
        if (split <= 0 || split == payload.Length - 1 ||
            !long.TryParse(payload[..split], NumberStyles.None, CultureInfo.InvariantCulture, out var missing) ||
            !long.TryParse(payload[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var evicted) || missing < 0 || evicted < 0) return false;
        integrity = new(missing, evicted); return true;
    }
    public static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
