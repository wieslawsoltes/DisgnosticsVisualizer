using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

/// <summary>
/// Retains original metadata-only native frames for late viewers, independently of the event tail.
/// Caller serializes access. No new source sequence is synthesized and no event-bearing frame is
/// repurposed as metadata (which would make deduplication discard the original events).
/// </summary>
public sealed class NativeBootstrapCache(int maximumBytes = 8 * 1024 * 1024)
{
    private sealed record Entry(long Sequence, byte[] Bytes);
    private Dictionary<(int Kind, int Key), Entry> _entries = [];
    private Guid _session;
    private bool _native;
    public byte[][] Snapshot() => _entries.Values.Distinct().OrderBy(e => e.Sequence).Select(e => e.Bytes.ToArray()).ToArray();
    public void Observe(TraceBlock block, ReadOnlySpan<byte> frame)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (block.SessionId != _session) { _entries.Clear(); _native = false; _session = block.SessionId; }
        if (block.Events.Length != 0 || block.Snapshot is not null) return;
        var keys = new List<(int Kind, int Key)>();
        foreach (var s in block.Symbols)
        {
            if (s.Name.StartsWith(NativeMetadataSymbol.Prefix, StringComparison.Ordinal))
            {
                if (!NativeMetadataSymbol.TryDecode(s.Name, out var metadata)) throw new InvalidDataException("Malformed native bootstrap metadata.");
                if (metadata is NativeProcessInfo) _native = true;
                keys.Add((metadata!.Kind, metadata.Key));
            }
        }
        if (!_native) return;
        foreach (var s in block.Symbols)
            if (SourceSiteSymbol.TryDecode(s.Name, out _)) keys.Add((5, s.Id));
        if (keys.Count == 0) return;
        var changed = keys.Where(k => !_entries.TryGetValue(k, out var e) || e.Sequence < block.Sequence).Distinct().ToArray();
        foreach (var key in keys)
            if (_entries.TryGetValue(key, out var old) && old.Sequence == block.Sequence && !frame.SequenceEqual(old.Bytes))
                throw new InvalidDataException("Native bootstrap replay changed content.");
        if (changed.Length == 0) return;
        var candidate = new Dictionary<(int, int), Entry>(_entries); var entry = new Entry(block.Sequence, frame.ToArray());
        foreach (var key in changed) candidate[key] = entry;
        if (candidate.Count > NativeMetadataSymbol.MaximumEntries + 4096 || candidate.Values.Distinct().Sum(e => (long)e.Bytes.Length) > maximumBytes)
            throw new InvalidDataException("Native bootstrap exceeds the bounded collector budget.");
        _entries = candidate;
    }
}
