using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

/// <summary>
/// Arrival-bounded storage with incrementally merged chronological publication. All methods
/// run under the owning store's lock. Published arrays never expose the private merge cache.
/// </summary>
internal sealed class RetainedEventIndex(int capacity)
{
    private sealed class Chunk(TraceEvent[] events, long generation)
    {
        public TraceEvent[] Events = events;
        public readonly long Generation = generation;
        public int Start;
    }
    private sealed class ChronologicalComparer : IComparer<TraceEvent>
    {
        public static readonly ChronologicalComparer Instance = new();
        public int Compare(TraceEvent x, TraceEvent y)
        {
            var timestamp = x.Timestamp.CompareTo(y.Timestamp);
            return timestamp != 0 ? timestamp : x.Id.CompareTo(y.Id);
        }
    }
    private readonly Queue<Chunk> _chunks = new();
    private readonly HashSet<long> _ids = [];
    private readonly HashSet<long> _expiredCached = [];
    private TraceEvent[] _sorted = [];
    private long _generation, _sortedThrough;
    private int _count;
    public int Count => _count;
    public long Evicted { get; private set; }

    public void Add(TraceEvent[] events)
    {
        var accepted = events.Where(e => _ids.Add(e.Id)).ToArray();
        if (accepted.Length == 0) return;
        _chunks.Enqueue(new(accepted, ++_generation));
        var count = (long)_count + accepted.Length;
        var excess = count - capacity;
        while (excess > 0)
        {
            var chunk = _chunks.Peek();
            var remove = (int)Math.Min(excess, chunk.Events.Length - chunk.Start);
            var end = chunk.Start + remove;
            for (var i = chunk.Start; i < end; i++)
            {
                var id = chunk.Events[i].Id; _ids.Remove(id);
                if (chunk.Generation <= _sortedThrough) _expiredCached.Add(id);
            }
            chunk.Start = end; excess -= remove; count -= remove; Evicted += remove;
            if (chunk.Start == chunk.Events.Length) _chunks.Dequeue();
            else if (chunk.Start >= 4096 && chunk.Start >= chunk.Events.Length / 2)
            {
                // A tiny tail must not retain a very large input batch indefinitely.
                chunk.Events = chunk.Events.AsSpan(chunk.Start).ToArray(); chunk.Start = 0;
            }
        }
        _count = (int)count;
        if (_ids.EnsureCapacity(0) > Math.Max(256L, (long)capacity * 2)) _ids.TrimExcess();
    }

    public TraceEvent[] Publish()
    {
        if (_generation != _sortedThrough)
        {
            // Only newly retained arrivals need sorting. Earlier runs are already sorted;
            // evicted cached identities are filtered even if the ID is later reintroduced.
            var additions = new List<TraceEvent>();
            foreach (var chunk in _chunks)
                if (chunk.Generation > _sortedThrough)
                    additions.AddRange(chunk.Events.AsSpan(chunk.Start));
            var delta = additions.ToArray();
            Array.Sort(delta, ChronologicalComparer.Instance);
            var merged = new TraceEvent[_count];
            var old = 0; var next = 0; var output = 0;
            if (_expiredCached.Count == 0 && _sorted.Length > 0)
            {
                // Nearly all old events normally precede the next short capture batch.
                // Copy that prefix once instead of comparing every old event again.
                var prefix = delta.Length == 0 ? _sorted.Length : LowerBound(_sorted, delta[0]);
                Array.Copy(_sorted, merged, prefix); old = output = prefix;
            }
            while (old < _sorted.Length || next < delta.Length)
            {
                while (old < _sorted.Length && _expiredCached.Contains(_sorted[old].Id)) old++;
                if (old == _sorted.Length && next == delta.Length) break;
                merged[output++] = old < _sorted.Length && (next == delta.Length || ChronologicalComparer.Instance.Compare(_sorted[old], delta[next]) <= 0)
                    ? _sorted[old++] : delta[next++];
            }
            if (output != _count) throw new InvalidOperationException("Retained trace index is inconsistent.");
            _sorted = merged; _sortedThrough = _generation; _expiredCached.Clear();
        }
        // TraceView has a public array for compatibility. Never allow a consumer to mutate
        // the baseline that the next publication will merge against.
        return (TraceEvent[])_sorted.Clone();
    }

    private static int LowerBound(TraceEvent[] events, TraceEvent value)
    {
        var low = 0; var high = events.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (ChronologicalComparer.Instance.Compare(events[middle], value) < 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
