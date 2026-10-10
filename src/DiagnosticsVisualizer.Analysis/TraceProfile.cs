using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public readonly record struct TraceRange(long Start, long End)
{
    public long Duration => End <= Start ? 0 : End - Start;
    public bool Intersects(in TraceEvent e) => e.Kind == TraceEventKind.Span
        ? e.Timestamp < End && e.End > Start : e.Timestamp >= Start && e.Timestamp < End;
    public long Clip(in TraceEvent e) => Math.Max(0, Math.Min(End, e.End) - Math.Max(Start, e.Timestamp));
}

public sealed record ProfileRow(string Name, Subsystem Subsystem, int Count, double TotalMs,
    double SelfMs, double AverageMs, double P50Ms, double P95Ms, double P99Ms, double MaximumMs, long EventId);
public sealed record CallTreeRow(string Operation, Subsystem Subsystem, int Depth, int Count,
    double TotalMs, double SelfMs, long EventId, bool Truncated = false);
public sealed record ThreadActivity(int ThreadId, int Events, double BusyMs, double OccupancyPercent);
public sealed record ActivityBucket(long Start, long End, double CpuMs, double BusiestThreadMs, int Events);
public sealed record ProfileResult(ProfileRow[] Operations, CallTreeRow[] TopDown, CallTreeRow[] BottomUp,
    ThreadActivity[] Threads, ActivityBucket[] Activity, TraceRange Range, int Events, bool Truncated);

/// <summary>
/// Per-refresh indexes shared by statistics and calling-context views. Self time subtracts the union
/// of same-thread child spans, clipped to the selected half-open range. Causal continuations are not
/// fabricated into synchronous call stacks. All graph traversals and output collections are bounded.
/// </summary>
public sealed class TraceProfile
{
    private readonly TraceView _trace;
    private readonly Dictionary<long, TraceEvent> _byId;
    private readonly Dictionary<long, List<TraceEvent>> _children = [];
    private const int MaxDepth = 64, MaxCallNodes = 100_000;
    public TraceProfile(TraceView trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(trace));
        _trace = trace;
        _byId = trace.Events.ToDictionary(e => e.Id);
        foreach (var e in trace.Events)
        {
            if (e.Kind != TraceEventKind.Span || !_byId.TryGetValue(e.ParentId, out var parent) ||
                parent.Kind != TraceEventKind.Span || parent.ThreadId != e.ThreadId) continue;
            if (!_children.TryGetValue(parent.Id, out var children)) _children[parent.Id] = children = [];
            children.Add(e);
        }
        foreach (var children in _children.Values) children.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }
    public long SelfTicks(in TraceEvent e, TraceRange range)
    {
        var start = Math.Max(e.Timestamp, range.Start); var end = Math.Min(e.End, range.End);
        if (e.Kind != TraceEventKind.Span || end <= start) return 0;
        if (!_children.TryGetValue(e.Id, out var children)) return end - start;
        var cursor = start; long covered = 0;
        foreach (var child in children)
        {
            var a = Math.Max(start, child.Timestamp); var b = Math.Min(end, child.End);
            if (b <= cursor || a >= end) continue;
            covered += b - Math.Max(a, cursor); cursor = b;
        }
        return Math.Max(0, end - start - covered);
    }
    public ProfileResult Analyze(TraceRange? selection = null, double budgetMs = 1000d / 60)
    {
        if (!double.IsFinite(budgetMs) || budgetMs <= 0 || budgetMs > 60_000)
            throw new ArgumentOutOfRangeException(nameof(budgetMs));
        var range = selection ?? (_trace.Events.Length == 0 ? new TraceRange(0, 1) :
            new TraceRange(_trace.Events.Min(e => e.Timestamp), Math.Max(_trace.Events.Max(e => e.End), _trace.Events.Min(e => e.Timestamp) + 1)));
        if (range.End <= range.Start || range.End - range.Start <= 0) throw new ArgumentOutOfRangeException(nameof(selection));
        var events = _trace.Events.Where(e => range.Intersects(e)).ToArray();
        var spans = events.Where(e => e.Kind == TraceEventKind.Span && e.Duration > 0).ToArray();
        var self = spans.ToDictionary(e => e.Id, e => SelfTicks(e, range));
        var operations = spans.GroupBy(e => (e.NameId, e.Subsystem)).Select(group =>
        {
            var durations = group.Select(e => range.Clip(e)).Order().ToArray();
            var longest = group.MaxBy(e => range.Clip(e));
            var total = durations.Sum(t => (double)t);
            return new ProfileRow(_trace.Name(longest), group.Key.Subsystem, durations.Length,
                Ms(total), Ms(group.Sum(e => (double)self[e.Id])), Ms(total / durations.Length),
                Ms(Quantile(durations, .5)), Ms(Quantile(durations, .95)), Ms(Quantile(durations, .99)), Ms(durations[^1]), longest.Id);
        }).OrderByDescending(row => row.SelfMs).ToArray();
        var top = new CallNode(0, Subsystem.Application); var bottom = new CallNode(0, Subsystem.Application);
        var count = 0; var truncated = false;
        var path = new List<TraceEvent>(MaxDepth);
        var visited = new HashSet<long>();
        foreach (var e in spans)
        {
            path.Clear(); visited.Clear(); visited.Add(e.Id); path.Add(e); var current = e;
            while (path.Count < MaxDepth && _byId.TryGetValue(current.ParentId, out var parent) &&
                parent.Kind == TraceEventKind.Span && parent.ThreadId == e.ThreadId &&
                parent.Timestamp <= current.Timestamp && parent.End >= current.End)
            {
                if (!visited.Add(parent.Id)) { truncated = true; break; }
                path.Add(parent); current = parent;
            }
            if (path.Count == MaxDepth && current.ParentId != 0) truncated = true;
            var node = top;
            for (var i = path.Count - 1; i >= 0; i--)
            {
                node = GetChild(node, path[i]);
                if (node is null) break;
                if (i != 0) continue;
                node.Count++; node.Total += range.Clip(e); node.Self += self[e.Id]; node.EventId = e.Id;
            }
            // Each event's exclusive work is attributed through its caller chain exactly once.
            node = bottom;
            for (var i = 0; i < path.Count; i++)
            {
                node = GetChild(node, path[i]); if (node is null) break;
                node.Count++; node.Total += self[e.Id];
                if (i == 0) node.Self += self[e.Id]; node.EventId = e.Id;
            }
        }
        var activity = BuildActivity(spans, events, range, budgetMs, out var threads);
        return new(operations, Flatten(top), Flatten(bottom), threads, activity, range, events.Length, truncated);

        CallNode? GetChild(CallNode owner, TraceEvent e)
        {
            var key = (e.NameId, e.Subsystem);
            if (owner.Children.TryGetValue(key, out var existing)) return existing;
            if (count >= MaxCallNodes) { truncated = true; return null; }
            count++;
            var added = new CallNode(e.NameId, e.Subsystem) { EventId = e.Id };
            owner.Children[key] = added; return added;
        }
    }
    private ActivityBucket[] BuildActivity(TraceEvent[] spans, TraceEvent[] all, TraceRange range,
        double budgetMs, out ThreadActivity[] threads)
    {
        var width = Math.Max(1d, Math.Max(budgetMs * _trace.Frequency / 1000, range.Duration / 2048d));
        var length = Math.Clamp((int)Math.Ceiling(range.Duration / width), 1, 2048);
        var cpu = new double[length]; var peak = new double[length]; var counts = new int[length];
        foreach (var e in all) counts[Math.Clamp((int)((Math.Max(range.Start, e.Timestamp) - (double)range.Start) / width), 0, length - 1)]++;
        var threadRows = new List<ThreadActivity>();
        var threadCounts = all.GroupBy(e => e.ThreadId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var group in spans.GroupBy(e => e.ThreadId))
        {
            var occupancy = new double[length]; var difference = new double[length + 1];
            long start = 0, end = 0; var active = false; double busy = 0;
            foreach (var e in group.OrderBy(e => e.Timestamp))
            {
                var a = Math.Max(range.Start, e.Timestamp); var b = Math.Min(range.End, e.End);
                if (b <= a) continue;
                if (!active) { start = a; end = b; active = true; }
                else if (a <= end) end = Math.Max(end, b);
                else { AddInterval(start, end); start = a; end = b; }
            }
            if (active) AddInterval(start, end);
            double running = 0;
            for (var i = 0; i < length; i++)
            {
                running += difference[i]; occupancy[i] += running * width;
                cpu[i] += occupancy[i]; peak[i] = Math.Max(peak[i], occupancy[i]);
            }
            threadRows.Add(new(group.Key, threadCounts.GetValueOrDefault(group.Key), Ms(busy), Math.Min(100, busy * 100 / range.Duration)));
            void AddInterval(long a, long b)
            {
                busy += b - a;
                var first = Math.Clamp((int)((a - (double)range.Start) / width), 0, length - 1);
                var last = Math.Clamp((int)Math.Ceiling((b - (double)range.Start) / width) - 1, first, length - 1);
                if (first == last) { occupancy[first] += b - a; return; }
                occupancy[first] += range.Start + (first + 1) * width - a;
                occupancy[last] += b - (range.Start + last * width);
                if (last > first + 1) { difference[first + 1]++; difference[last]--; }
            }
        }
        foreach (var (thread, count) in threadCounts)
            if (!threadRows.Any(row => row.ThreadId == thread)) threadRows.Add(new(thread, count, 0, 0));
        threads = threadRows.OrderByDescending(t => t.BusyMs).ToArray();
        return Enumerable.Range(0, length).Select(i => new ActivityBucket(
            range.Start + (long)(i * width), Math.Min(range.End, range.Start + (long)((i + 1) * width)), Ms(cpu[i]), Ms(peak[i]), counts[i])).ToArray();
    }
    private CallTreeRow[] Flatten(CallNode root)
    {
        var result = new List<CallTreeRow>(); var stack = new Stack<(CallNode Node, int Depth)>();
        foreach (var child in root.Children.Values.OrderBy(c => c.Total)) stack.Push((child, 0));
        while (stack.Count > 0 && result.Count < MaxCallNodes)
        {
            var (node, depth) = stack.Pop(); var name = _trace.Symbols.GetValueOrDefault(node.NameId, $"site #{node.NameId}");
            result.Add(new(new string(' ', depth * 3) + name, node.Subsystem, depth, node.Count, Ms(node.Total), Ms(node.Self), node.EventId));
            foreach (var child in node.Children.Values.OrderBy(c => c.Total)) stack.Push((child, depth + 1));
        }
        return result.ToArray();
    }
    private double Ms(double ticks) => ticks * (1000d / _trace.Frequency);
    private static double Quantile(long[] values, double percentile)
    {
        var index = (values.Length - 1) * percentile; var lower = (int)index;
        return values[lower] + (values[Math.Min(lower + 1, values.Length - 1)] - (double)values[lower]) * (index - lower);
    }
    private sealed class CallNode(int nameId, Subsystem subsystem)
    {
        public int NameId { get; } = nameId;
        public Subsystem Subsystem { get; } = subsystem;
        public Dictionary<(int, Subsystem), CallNode> Children { get; } = [];
        public int Count;
        public double Total, Self;
        public long EventId;
    }
}
