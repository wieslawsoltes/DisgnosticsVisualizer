using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

/// <summary>Sorted interval index with subtree max-end pruning; long spans remain visible when panning.</summary>
public sealed class IntervalIndex
{
    private readonly TraceEvent[] _events;
    private readonly long[] _maxEnd;
    public IntervalIndex(IEnumerable<TraceEvent> events)
    {
        _events = events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToArray();
        _maxEnd = new long[Math.Max(1, _events.Length * 4)];
        if (_events.Length > 0) Build(1, 0, _events.Length);
    }
    private long Build(int node, int left, int right)
    {
        if (right - left == 1) return _maxEnd[node] = Math.Max(_events[left].Timestamp, _events[left].End);
        var middle = left + (right - left) / 2;
        return _maxEnd[node] = Math.Max(Build(node * 2, left, middle), Build(node * 2 + 1, middle, right));
    }
    public List<TraceEvent> Query(long start, long end, int maxResults = 200_000)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        var result = new List<TraceEvent>();
        if (_events.Length > 0 && maxResults > 0) Query(1, 0, _events.Length, start, end, result, maxResults);
        return result;
    }
    private void Query(int node, int left, int right, long start, long end, List<TraceEvent> result, int limit)
    {
        if (result.Count >= limit || _maxEnd[node] < start || _events[left].Timestamp > end) return;
        if (right - left == 1) { result.Add(_events[left]); return; }
        var middle = left + (right - left) / 2;
        Query(node * 2, left, middle, start, end, result, limit);
        Query(node * 2 + 1, middle, right, start, end, result, limit);
    }
}
