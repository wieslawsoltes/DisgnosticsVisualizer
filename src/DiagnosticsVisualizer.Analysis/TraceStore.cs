using System.Collections.ObjectModel;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record SnapshotCheckpoint(long Sequence, long EventWatermark, SnapshotData Data);

public sealed record TraceView(Guid SessionId, long Frequency, long OriginTimestamp,
    TraceEvent[] Events, IReadOnlyDictionary<int, string> Symbols,
    IReadOnlyDictionary<long, ObjectInfo> Objects, SnapshotData Snapshot,
    long DroppedEvents, long MissingBlocks, long ProbeFaults, long MetadataDrops,
    long EvictedEvents, bool Recording)
{
    /// <summary>UTC anchor from the first accepted block; legacy producers can timestamp later blocks independently.</summary>
    public long UtcStartTicks { get; init; }
    public IReadOnlyDictionary<int, SourceSite> SourceSites { get; init; } = new Dictionary<int, SourceSite>();
    public IReadOnlyDictionary<(int Kind, int Key), NativeMetadata> NativeMetadata { get; init; } = new Dictionary<(int, int), NativeMetadata>();
    public SnapshotCheckpoint[] Checkpoints { get; init; } = [];
    public string Name(in TraceEvent e) => Symbols.TryGetValue(e.NameId, out var name) ? name : $"site #{e.NameId}";
    public double Milliseconds(long ticks) => ticks * (1000d / Frequency);
}

/// <summary>Thread-safe bounded ingestion. Event-id de-duplication survives reordered UDP batches.</summary>
public sealed class TraceStore
{
    private readonly object _gate = new();
    private readonly RetainedEventIndex _events;
    private readonly TraceSequenceLedger _sequences;
    private readonly Dictionary<int, string> _symbols = [], _displaySymbols = [];
    private readonly Dictionary<int, SourceSite> _sourceSites = [];
    private readonly HashSet<int> _integritySymbols = [];
    private readonly HashSet<int> _nativeSymbols = [];
    private readonly Dictionary<(int Kind, int Key), (long Sequence, NativeMetadata Value)> _native = [];
    private IReadOnlyDictionary<(int Kind, int Key), NativeMetadata>? _publishedNative;
    private readonly Dictionary<long, ObjectInfo> _objects = [];
    private IReadOnlyDictionary<int, string>? _publishedSymbols;
    private IReadOnlyDictionary<int, SourceSite>? _publishedSites;
    private IReadOnlyDictionary<long, ObjectInfo>? _publishedObjects;
    private Guid _session;
    private long _frequency = 10_000_000, _origin, _drops, _faults, _metadataDrops, _utc;
    private long _inheritedMissing, _inheritedEvicted;
    private SnapshotData _snapshot = SnapshotData.Empty;
    private long _snapshotSequence;
    private bool _recording;
    private readonly Queue<SnapshotCheckpoint> _checkpoints = new();
    private long _watermark;
    private int _snapshotNodes;
    public TraceStore(int maxEvents = 2_000_000) : this(maxEvents, 1_000_000) { }
    public TraceStore(int maxEvents, int sequenceCapacity)
    {
        if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (sequenceCapacity is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(sequenceCapacity));
        _events = new(maxEvents); _sequences = new(sequenceCapacity);
    }
    public int Count { get { lock (_gate) return _events.Count; } }
    public void Add(TraceBlock block)
    {
        lock (_gate)
        {
            if (_session != Guid.Empty && _session != block.SessionId) throw new InvalidDataException("Cannot combine different source sessions.");
            if (_session != Guid.Empty && (_frequency != block.Frequency || _origin != block.OriginTimestamp))
                throw new InvalidDataException("Session clock changed.");
            // Reject malformed reserved metadata before accepting the batch or altering counters.
            var provenance = block.Symbols.Where(s => s.Name.StartsWith(TraceIntegritySymbol.Prefix, StringComparison.Ordinal)).ToArray();
            foreach (var symbol in provenance)
                if (!TraceIntegritySymbol.TryDecode(symbol.Name, out _) || block.Events.Length != 0 || block.Snapshot is not null)
                    throw new InvalidDataException("Invalid capture integrity metadata frame.");
            foreach (var symbol in block.Symbols)
                if (_symbols.TryGetValue(symbol.Id, out var oldName) && oldName != symbol.Name)
                    throw new InvalidDataException("Symbol identity collision.");
            if (_integritySymbols.Count > 0 && block.Events.Any(e => _integritySymbols.Contains(e.NameId)))
                throw new InvalidDataException("An event cannot refer to reserved capture integrity metadata.");
            if (provenance.Length > 0)
            {
                var identities = block.Symbols.Select(s => s.Id).ToHashSet();
                if (identities.Count != block.Symbols.Length || _symbols.Count + identities.Count(id => !_symbols.ContainsKey(id)) > 1_000_000)
                    throw new InvalidDataException("Duplicate or unretainable capture integrity metadata.");
            }
            var native = new Dictionary<int, NativeMetadata>();
            foreach (var symbol in block.Symbols.Where(s => s.Name.StartsWith(NativeMetadataSymbol.Prefix, StringComparison.Ordinal)))
            {
                if (!NativeMetadataSymbol.TryDecode(symbol.Name, out var entry) || block.Events.Length != 0 || block.Snapshot is not null || !native.TryAdd(symbol.Id, entry!))
                    throw new InvalidDataException("Invalid native provenance frame.");
                if (_native.TryGetValue((entry!.Kind, entry.Key), out var oldEntry) &&
                    (entry is NativeProcessInfo or NativeThreadInfo || oldEntry.Sequence == block.Sequence) && oldEntry.Value != entry)
                    throw new InvalidDataException("Native provenance identity changed.");
            }
            if (native.Count != 0 && (block.Symbols.Select(s => s.Id).Distinct().Count() != block.Symbols.Length || native.Values.GroupBy(n => (n.Kind, n.Key)).Any(g => g.Distinct().Count() != 1)))
                throw new InvalidDataException("Conflicting native provenance in one batch.");
            if (_nativeSymbols.Count != 0 && block.Events.Any(e => _nativeSymbols.Contains(e.NameId)))
                throw new InvalidDataException("An event cannot use a native provenance symbol as a method.");
            if (native.Count != 0 && (_native.Keys.Concat(native.Values.Select(n => (n.Kind, n.Key))).Distinct().Count() > NativeMetadataSymbol.MaximumEntries ||
                native.Count != 0 && _symbols.Count + block.Symbols.Count(s => !_symbols.ContainsKey(s.Id)) > 1_000_000))
                throw new InvalidDataException("Native provenance capacity exceeded.");
            if (!_sequences.TryAccept(block.Sequence)) return;
            foreach (var (id, entry) in native)
            {
                _nativeSymbols.Add(id);
                if (!_native.TryGetValue((entry.Kind, entry.Key), out var oldEntry) || block.Sequence > oldEntry.Sequence)
                { _native[(entry.Kind, entry.Key)] = (block.Sequence, entry); _publishedNative = null; }
            }
            _session = block.SessionId; _frequency = block.Frequency; _origin = block.OriginTimestamp; if (_utc == 0) _utc = block.UtcStartTicks;
            foreach (var s in block.Symbols)
            {
                if (_symbols.TryGetValue(s.Id, out var old) && old != s.Name) throw new InvalidDataException("Symbol identity collision.");
                if (!_symbols.ContainsKey(s.Id) && _symbols.Count < 1_000_000)
                {
                    _symbols[s.Id] = s.Name;
                    if (SourceSiteSymbol.TryDecode(s.Name, out var site))
                    {
                        _sourceSites[s.Id] = site!; _displaySymbols[s.Id] = site!.Signature;
                        _publishedSites = null; _publishedSymbols = null;
                    }
                    else if (TraceIntegritySymbol.TryDecode(s.Name, out var integrity))
                    {
                        _integritySymbols.Add(s.Id);
                        _inheritedMissing = Math.Max(_inheritedMissing, integrity.MissingBlocks);
                        _inheritedEvicted = Math.Max(_inheritedEvicted, integrity.EvictedEvents);
                    }
                    else if (!native.ContainsKey(s.Id)) { _displaySymbols[s.Id] = s.Name; _publishedSymbols = null; }
                }
            }
            foreach (var o in block.Objects)
            {
                if (_objects.TryGetValue(o.Id, out var previous))
                {
                    if (previous == o) continue;
                }
                else if (_objects.Count >= 1_000_000) continue;
                _objects[o.Id] = o; _publishedObjects = null;
            }
            if (provenance.Length != 0 && provenance.Any(s => !_symbols.ContainsKey(s.Id)))
                throw new InvalidDataException("Cannot retain capture integrity metadata: symbol budget exceeded.");
            _events.Add(block.Events);
            if (block.Events.Length > 0) _watermark = Math.Max(_watermark, block.Events.Max(e => e.End));
            if (block.Snapshot is not null && block.Sequence >= _snapshotSequence)
            {
                _snapshot = block.Snapshot; _snapshotSequence = block.Sequence;
                _checkpoints.Enqueue(new(block.Sequence, _watermark, block.Snapshot)); _snapshotNodes += block.Snapshot.Nodes.Length;
                while (_checkpoints.Count > 32 || (_snapshotNodes > 65536 && _checkpoints.Count > 1))
                    _snapshotNodes -= _checkpoints.Dequeue().Data.Nodes.Length;
            }
            _drops = Math.Max(_drops, block.DroppedEvents); _faults = Math.Max(_faults, block.ProbeFaults);
            _metadataDrops = Math.Max(_metadataDrops, block.MetadataDrops);
            if (block.Sequence == _sequences.Highest) _recording = block.Recording;
        }
    }
    public TraceView GetView()
    {
        lock (_gate)
        {
            var events = _events.Publish();
            var missing = _sequences.Missing;
            return new(_session, _frequency, _origin, events,
                _publishedSymbols ??= new ReadOnlyDictionary<int, string>(new Dictionary<int, string>(_displaySymbols)),
                _publishedObjects ??= new ReadOnlyDictionary<long, ObjectInfo>(new Dictionary<long, ObjectInfo>(_objects)), _snapshot, _drops, TraceIntegritySymbol.SaturatingAdd(missing, _inheritedMissing), _faults,
                _metadataDrops, TraceIntegritySymbol.SaturatingAdd(_events.Evicted, _inheritedEvicted), _recording) { UtcStartTicks = _utc, NativeMetadata = _publishedNative ??= new ReadOnlyDictionary<(int, int), NativeMetadata>(_native.ToDictionary(p => p.Key, p => p.Value.Value)), Checkpoints = _checkpoints.ToArray(), SourceSites = _publishedSites ??= new ReadOnlyDictionary<int, SourceSite>(new Dictionary<int, SourceSite>(_sourceSites)) };
        }
    }
}
