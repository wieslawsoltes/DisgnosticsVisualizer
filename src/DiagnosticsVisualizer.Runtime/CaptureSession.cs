using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

public sealed class CaptureSession : IDisposable
{
    internal readonly ConditionalWeakTable<object, ResourceLabel> ResourceLabels = new();
    internal readonly ConditionalWeakTable<Delegate, RoutedHandlerName> RoutedHandlers = new();
    internal readonly ConditionalWeakTable<object, DispatcherCapture> Dispatchers = new();
    private sealed record Identity(long Id);
    private readonly ConditionalWeakTable<object, Identity> _identities = new();
    private readonly ConcurrentDictionary<long, WeakReference<object>> _references = new();
    private readonly ConcurrentDictionary<long, ObjectInfo> _objects = new();
    private readonly ConcurrentDictionary<string, int> _symbolIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _symbols = new();
    private readonly ConcurrentQueue<CaptureWriter> _writers = new();
    private readonly ThreadLocal<CaptureWriter?> _local;
    private readonly ConditionalWeakTable<object, Identity>.CreateValueCallback _createIdentity;
    private readonly object _drainGate = new(), _symbolGate = new();
    private long _nextObject, _blockSequence, _drops, _faults, _metadataDrops;
    private int _nextSymbol, _nextWriter, _enabled = 1, _disposed, _drainStart;
    private SnapshotData? _snapshot;
    public Guid Id { get; } = Guid.NewGuid();
    public long OriginTimestamp { get; } = Stopwatch.GetTimestamp();
    public long UtcStartTicks { get; } = DateTime.UtcNow.Ticks;
    public CaptureOptions Options { get; }
    public bool Recording => Volatile.Read(ref _enabled) != 0 && Volatile.Read(ref _disposed) == 0;
    public long DroppedEvents => Interlocked.Read(ref _drops);
    public long ProbeFaults => Interlocked.Read(ref _faults);
    public long MetadataDrops => Interlocked.Read(ref _metadataDrops);
    public CaptureSession(CaptureOptions? options = null)
    {
        Options = options ?? new(); Options.Validate(); _createIdentity = CreateIdentity;
        _local = new ThreadLocal<CaptureWriter?>(() =>
        {
            var ordinal = Interlocked.Increment(ref _nextWriter);
            if (ordinal > Options.MaxThreads) { Drop(); return null; }
            var writer = new CaptureWriter(ordinal, Options.EventsPerThread); _writers.Enqueue(writer); return writer;
        });
    }
    public void SetRecording(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _enabled, enabled ? 1 : 0);
    }
    internal CaptureWriter? Writer => _local.Value;
    internal void Drop() => Interlocked.Increment(ref _drops);
    internal void Fault() => Interlocked.Increment(ref _faults);
    public long GetObjectId(object? value) => value is null ? 0 : _identities.GetValue(value, _createIdentity).Id;
    private Identity CreateIdentity(object value)
    {
        var id = Interlocked.Increment(ref _nextObject);
        if (id > Options.MaxObjects) { Interlocked.Increment(ref _metadataDrops); return new(0); }
        var type = value.GetType(); _objects[id] = new(id, type.FullName ?? type.Name, $"{type.Name} #{id}");
        _references[id] = new(value); return new(id);
    }
    internal void LabelProperty(long id, string? name)
    {
        if (id > 0 && !string.IsNullOrEmpty(name) && name.Length <= 512 && _objects.TryGetValue(id, out var info) && info.Display != name)
            _objects[id] = info with { Display = name };
    }
    public object? ResolveObject(long id) => _references.TryGetValue(id, out var weak) && weak.TryGetTarget(out var value) ? value : null;
    internal int Intern(CaptureWriter writer, string name)
    {
        if (writer.Symbols.TryGetValue(name, out var id)) return id;
        if (!_symbolIds.TryGetValue(name, out id))
        {
            lock (_symbolGate)
            {
                if (!_symbolIds.TryGetValue(name, out id))
                {
                    if (_nextSymbol >= Options.MaxSymbols) { Interlocked.Increment(ref _metadataDrops); return 0; }
                    id = ++_nextSymbol; _symbols[id] = name; _symbolIds[name] = id;
                }
            }
        }
        if (writer.Symbols.Count < Math.Min(Options.MaxSymbols, 8192)) writer.Symbols[name] = id;
        return id;
    }
    public void SetSnapshot(SnapshotData snapshot) => Interlocked.Exchange(ref _snapshot, snapshot);
    public TraceBlock Drain(int maxEvents = 4096)
    {
        if (maxEvents is < 1 or > TraceLimits.MaxEventsPerBlock) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        lock (_drainGate)
        {
            var events = new List<TraceEvent>(maxEvents); var writers = _writers.ToArray();
            if (writers.Length > 0)
            {
                var start = _drainStart++ % writers.Length;
                for (var i = 0; i < writers.Length && events.Count < maxEvents; i++) writers[(start + i) % writers.Length].Drain(events, maxEvents);
                if (_drainStart == int.MaxValue) _drainStart = 0;
            }
            var snapshot = Interlocked.Exchange(ref _snapshot, null);
            var symbols = events.Select(e => e.NameId).Distinct().Where(id => id != 0 && _symbols.ContainsKey(id))
                .Select(id => new Symbol(id, _symbols[id])).ToArray();
            var metadata = TraceObjectReferences.Enumerate(events, snapshot).Where(_objects.ContainsKey)
                .Take(TraceLimits.MaxObjectsPerBlock + 1).Select(id => _objects[id]).ToArray();
            if (metadata.Length > TraceLimits.MaxObjectsPerBlock) Interlocked.Increment(ref _metadataDrops);
            var objects = metadata.Take(TraceLimits.MaxObjectsPerBlock).ToArray();
            return new TraceBlock { SessionId = Id, Sequence = Interlocked.Increment(ref _blockSequence),
                Frequency = Stopwatch.Frequency, OriginTimestamp = OriginTimestamp, UtcStartTicks = UtcStartTicks,
                DroppedEvents = DroppedEvents, ProbeFaults = ProbeFaults, MetadataDrops = MetadataDrops,
                Recording = Recording, Symbols = symbols, Objects = objects, Events = events.ToArray(),
                Snapshot = snapshot };
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _enabled, 0); Probe.Detach(this); _local.Dispose();
    }
}
