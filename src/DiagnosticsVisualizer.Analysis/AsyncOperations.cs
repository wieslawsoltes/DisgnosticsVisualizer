using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record AsyncOperation(long Id, string Name, Subsystem Subsystem, TraceEvent? Begin,
    TraceEvent? Completion, string Status, double? DurationMs)
{
    public long EventId => Begin?.Id ?? Completion?.Id ?? 0;
    public long ObjectId => Begin?.ObjectId ?? Completion?.ObjectId ?? 0;
    public int StartThread => Begin?.ThreadId ?? 0;
    public int EndThread => Completion?.ThreadId ?? 0;
    public bool IsComplete => DurationMs is not null;
}

/// <summary>Linear correlation. Missing markers remain unknown, never fabricated success or zero latency.</summary>
public static class AsyncOperations
{
    public static AsyncOperation[] Analyze(TraceView trace)
    {
        var starts = new Dictionary<long, TraceEvent>();
        var ends = new Dictionary<long, TraceEvent>();
        var duplicates = new HashSet<long>();
        foreach (var e in trace.Events)
        {
            if (AsyncOperationEvent.IsBegin(e)) { if (!starts.TryAdd(e.Id, e)) duplicates.Add(e.Id); }
            else if (AsyncOperationEvent.IsEnd(e)) { if (!ends.TryAdd(e.ParentId, e)) duplicates.Add(e.ParentId); }
        }
        var result = new List<AsyncOperation>(starts.Count + ends.Count);
        foreach (var id in starts.Keys.Concat(ends.Keys.Where(id => !starts.ContainsKey(id))))
        {
            var hasStart = starts.TryGetValue(id, out var start); var hasEnd = ends.TryGetValue(id, out var end);
            double? duration = null;
            var status = !hasStart ? "Start not retained" : !hasEnd ? "Completion not recorded" : "Invalid lifecycle";
            if (duplicates.Contains(id)) status = "Conflicting markers";
            else if (hasStart && hasEnd && end.Timestamp >= start.Timestamp && start.Timestamp >= 0 &&
                start.NameId == end.NameId && start.Subsystem == end.Subsystem && start.ObjectId == end.ObjectId &&
                end.Data1 is >= (long)AsyncOperationOutcome.Succeeded and <= (long)AsyncOperationOutcome.Canceled)
            {
                status = ((AsyncOperationOutcome)end.Data1).ToString();
                duration = trace.Milliseconds(end.Timestamp - start.Timestamp);
            }
            var representative = hasStart ? start : end;
            result.Add(new(id, trace.Name(representative), representative.Subsystem, hasStart ? start : null,
                hasEnd ? end : null, status, duration));
        }
        return result.OrderByDescending(r => r.DurationMs ?? -1).ThenBy(r => r.Id).ToArray();
    }
}
