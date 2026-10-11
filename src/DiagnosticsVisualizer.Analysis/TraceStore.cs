using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record SnapshotCheckpoint(long Sequence, long EventWatermark, SnapshotData Data);

public sealed record TraceView(Guid SessionId, long Frequency, long OriginTimestamp,
    TraceEvent[] Events, IReadOnlyDictionary<int, string> Symbols,
    IReadOnlyDictionary<long, ObjectInfo> Objects, SnapshotData Snapshot,
    long DroppedEvents, long MissingBlocks, long ProbeFaults, long MetadataDrops,
    long EvictedEvents, bool Recording)
{
    public IReadOnlyDictionary<int, SourceSite> SourceSites { get; init; } = new Dictionary<int, SourceSite>();
    public SnapshotCheckpoint[] Checkpoints { get; init; } = [];
    public string Name(in TraceEvent e) => Symbols.TryGetValue(e.NameId, out var name) ? name : $"site #{e.NameId}";
    public double Milliseconds(long ticks) => ticks * (1000d / Frequency);
}

/// <summary>Thread-safe bounded ingestion. Event-id de-duplication survives reordered UDP batches.</summary>
public sealed class TraceStore(int maxEvents = 2_000_000)
{
    private readonly object _gate = new();
    private readonly Queue<TraceEvent[]> _chunks = new();
    private readonly HashSet<long> _ids = [];
    private readonly SortedSet<long> _sequences = [];
    private readonly Dictionary<int, string> _symbols = [], _displaySymbols = [];
    private readonly Dictionary<int, SourceSite> _sourceSites = [];
    private readonly Dictionary<long, ObjectInfo> _objects = [];
    private Guid _session;
    private long _frequency = 10_000_000, _origin, _drops, _faults, _metadataDrops, _evicted, _highestSequence;
    private int _count;
    private SnapshotData _snapshot = SnapshotData.Empty;
    private long _snapshotSequence;
    private bool _recording;
    private readonly Queue<SnapshotCheckpoint> _checkpoints = new();
    private long _watermark;
    private int _snapshotNodes;
    public int Count { get { lock (_gate) return _count; } }
    public void Add(TraceBlock block)
    {
        lock (_gate)
        {
            if (_session != Guid.Empty && _session != block.SessionId) throw new InvalidDataException("Cannot combine different source sessions.");
            if (_session != Guid.Empty && (_frequency != block.Frequency || _origin != block.OriginTimestamp))
                throw new InvalidDataException("Session clock changed.");
            if (!_sequences.Add(block.Sequence)) return;
            _session = block.SessionId; _frequency = block.Frequency; _origin = block.OriginTimestamp;
            _highestSequence = Math.Max(_highestSequence, block.Sequence);
            // Sequence tracking is bounded; old sequence replays are rejected by the transport.
            if (_sequences.Count > 1_000_000) _sequences.Remove(_sequences.Min);
            foreach (var s in block.Symbols)
            {
                if (_symbols.TryGetValue(s.Id, out var old) && old != s.Name) throw new InvalidDataException("Symbol identity collision.");
                if (!_symbols.ContainsKey(s.Id) && _symbols.Count < 1_000_000)
                {
                    _symbols[s.Id] = s.Name;
                    if (SourceSiteSymbol.TryDecode(s.Name, out var site))
                    { _sourceSites[s.Id] = site!; _displaySymbols[s.Id] = site!.Signature; }
                    else _displaySymbols[s.Id] = s.Name;
                }
            }
            foreach (var o in block.Objects) if (_objects.Count < 1_000_000) _objects[o.Id] = o;
            var accepted = block.Events.Where(e => _ids.Add(e.Id)).ToArray();
            if (accepted.Length > 0) { _chunks.Enqueue(accepted); _count += accepted.Length; }
            while (_count > maxEvents && _chunks.Count > 0)
            {
                var removed = _chunks.Dequeue(); _count -= removed.Length; _evicted += removed.Length;
                foreach (var e in removed) _ids.Remove(e.Id);
            }
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
            if (block.Sequence == _highestSequence) _recording = block.Recording;
        }
    }
    public TraceView GetView()
    {
        lock (_gate)
        {
            var events = _chunks.SelectMany(chunk => chunk).OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToArray();
            var missing = _sequences.Count == 0 ? 0 : Math.Max(0, _highestSequence - _sequences.Min + 1 - _sequences.Count);
            return new(_session, _frequency, _origin, events, new Dictionary<int, string>(_displaySymbols),
                new Dictionary<long, ObjectInfo>(_objects), _snapshot, _drops, missing, _faults,
                _metadataDrops, _evicted, _recording) { Checkpoints = _checkpoints.ToArray(), SourceSites = new Dictionary<int, SourceSite>(_sourceSites) };
        }
    }
}
