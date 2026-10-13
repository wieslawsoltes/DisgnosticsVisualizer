using System.Text;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

/// <summary>
/// Bounded late-join provenance. Materialized metadata is not an original target event/checkpoint.
/// The cache is single-owner; callers synchronize it with history/session changes.
/// </summary>
public sealed class NativeMetadataCache
{
    private const int ByteBudget = 2 * 1024 * 1024, ChunkBudget = 256 * 1024;
    private readonly Dictionary<int, Symbol> _sites = [];
    private readonly Dictionary<(int Kind, int Key), (long Sequence, Symbol Symbol, NativeMetadata Value)> _metadata = [];
    private TraceBlock? _clock;
    private int _bytes;
    public bool Active => _clock is not null;
    public void Reset() { _sites.Clear(); _metadata.Clear(); _clock = null; _bytes = 0; }
    public void Observe(TraceBlock block)
    {
        // Managed-only sessions must not pay for native staging dictionaries on every batch.
        if (_clock is null && !block.Symbols.Any(s => s.Name.StartsWith(NativeMetadataSymbol.Prefix, StringComparison.Ordinal))) return;
        if (_clock is { } established && (block.SessionId != established.SessionId || block.Frequency != established.Frequency ||
            block.OriginTimestamp != established.OriginTimestamp || block.UtcStartTicks != established.UtcStartTicks))
            throw new InvalidDataException("Native relay source clock changed.");
        var clock = _clock; var bytes = _bytes;
        var pending = new Dictionary<(int Kind, int Key), (long Sequence, Symbol Symbol, NativeMetadata Value)>();
        var sites = new Dictionary<int, Symbol>();
        foreach (var symbol in block.Symbols)
        {
            if (!symbol.Name.StartsWith(NativeMetadataSymbol.Prefix, StringComparison.Ordinal)) continue;
            if (!NativeMetadataSymbol.TryDecode(symbol.Name, out var value)) throw new InvalidDataException("Invalid native relay provenance.");
            if (clock is null)
            {
                if (value is not NativeProcessInfo) throw new InvalidDataException("Native provenance requires its process header first.");
                clock = block with { Events = [], Symbols = [], Objects = [], Snapshot = null };
            }
            if (block.SessionId != clock.SessionId || block.Frequency != clock.Frequency || block.OriginTimestamp != clock.OriginTimestamp)
                throw new InvalidDataException("Native relay source clock changed.");
            var key = (value!.Kind, value.Key);
            if (pending.ContainsKey(key)) throw new InvalidDataException("Duplicate native provenance key.");
            if (_metadata.TryGetValue(key, out var previous))
            {
                if (value is NativeProcessInfo or NativeThreadInfo && value != previous.Value)
                    throw new InvalidDataException("Native process/thread identity changed.");
                if (previous.Sequence >= block.Sequence) continue;
            }
            bytes += Size(symbol) - (previous.Symbol is null ? 0 : Size(previous.Symbol));
            pending.Add(key, (block.Sequence, symbol, value));
        }
        if (clock is null) return;
        foreach (var symbol in block.Symbols)
        {
            if (!SourceSiteSymbol.TryDecode(symbol.Name, out _)) continue;
            if (symbol.Id is < 1 or > 4096) throw new InvalidDataException("Native method identity exceeds its budget.");
            if (_sites.TryGetValue(symbol.Id, out var previous) || sites.TryGetValue(symbol.Id, out previous))
            { if (previous != symbol) throw new InvalidDataException("Native method identity collision."); continue; }
            sites.Add(symbol.Id, symbol); bytes += Size(symbol);
        }
        if (_metadata.Count + pending.Keys.Count(k => !_metadata.ContainsKey(k)) > NativeMetadataSymbol.MaximumEntries || bytes > ByteBudget)
            throw new InvalidDataException("Native provenance budget exceeded.");
        // Commit only after validating the whole frame: malformed updates cannot poison a late join.
        _clock = clock; _bytes = bytes;
        foreach (var entry in pending) _metadata[entry.Key] = entry.Value;
        foreach (var entry in sites) _sites[entry.Key] = entry.Value;
    }
    /// <summary>Assigns metadata-only sequences directly before the retained tail, without inventing missing history.</summary>
    public TraceBlock[] MaterializeBefore(long firstRetainedSequence)
    {
        if (_clock is null) return [];
        var chunks = new List<Symbol[]>(); var chunk = new List<Symbol>(); var bytes = 0;
        foreach (var symbol in _metadata.OrderBy(p => p.Key).Select(p => p.Value.Symbol).Concat(_sites.OrderBy(p => p.Key).Select(p => p.Value)))
        {
            var cost = Size(symbol);
            if (chunk.Count != 0 && (bytes + cost > ChunkBudget || chunk.Count >= TraceLimits.MaxSymbolsPerBlock))
            { chunks.Add(chunk.ToArray()); chunk.Clear(); bytes = 0; }
            chunk.Add(symbol); bytes += cost;
        }
        if (chunk.Count != 0) chunks.Add(chunk.ToArray());
        // The original header is still in a very short history; never collide with its sequence.
        if (firstRetainedSequence <= chunks.Count) return [];
        return chunks.Select((symbols, i) => _clock with
        {
            Sequence = firstRetainedSequence - chunks.Count + i, Symbols = symbols,
            Recording = !_metadata.ContainsKey((4, 0))
        }).ToArray();
    }
    private static int Size(Symbol symbol) => Encoding.UTF8.GetByteCount(symbol.Name) + 16;
}
