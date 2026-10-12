using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record DispatcherOperation(long Id, string Name, TraceEvent? Queued, TraceEvent? Started,
    TraceEvent? Finished, string Status, double? QueueMs, double? ExecutionMs)
{
    public double? RunnableQueueMs { get; init; }
    public double? InactiveQueueMs { get; init; }
    public TraceEvent[] PriorityChanges { get; init; } = [];
    public long ObjectId => Queued?.ObjectId ?? Started?.ObjectId ?? Finished?.ObjectId ?? 0;
    public long EventId => Started?.Id ?? Queued?.Id ?? Finished?.Id ?? 0;
    public int PostingThread => Queued?.ThreadId ?? 0;
    public int ExecutionThread => Started?.ThreadId ?? 0;
    public int? PostedPriority => Queued is { } e ? DispatcherEvent.Priority(e.Data1) : null;
    public int? ExecutionPriority => Started is { } e ? DispatcherEvent.Priority(e.Data1) : null;
}

public static class DispatcherOperations
{
    public static DispatcherOperation[] Analyze(TraceView trace)
    {
        var queued = new Dictionary<long, TraceEvent>(); var started = new Dictionary<long, TraceEvent>();
        var finished = new Dictionary<long, TraceEvent>(); var conflicts = new HashSet<long>();
        var changes = new Dictionary<long, List<TraceEvent>>();
        foreach (var e in trace.Events)
        {
            if (!DispatcherEvent.IsMarker(e)) continue;
            var id = e.Data0 == DispatcherEvent.QueuedCode ? e.Id : e.ParentId;
            if (e.Data0 == DispatcherEvent.PriorityCode)
            { if (!changes.TryGetValue(id, out var list)) changes[id] = list = []; list.Add(e); continue; }
            var map = e.Data0 == DispatcherEvent.QueuedCode ? queued : e.Data0 == DispatcherEvent.StartedCode ? started : finished;
            if (id == 0 || !map.TryAdd(id, e)) conflicts.Add(id);
        }
        var result = new List<DispatcherOperation>();
        foreach (var id in queued.Keys.Concat(started.Keys).Concat(finished.Keys).Concat(changes.Keys).Distinct())
        {
            var q = queued.TryGetValue(id, out var admission); var s = started.TryGetValue(id, out var invocation); var f = finished.TryGetValue(id, out var completion);
            var transitions = changes.GetValueOrDefault(id)?.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToArray() ?? [];
            double? wait = null, run = null, ready = null, inactive = null; var status = "Incomplete / not retained";
            var representative = s ? invocation : q ? admission : f ? completion : transitions[0];
            var invalid = (q && !DispatcherEvent.IsValidPriority(admission.Data1)) || (s && !DispatcherEvent.IsValidPriority(invocation.Data1)) || conflicts.Contains(id) || representative.ObjectId == 0 ||
                (q && admission.Timestamp < 0) || (s && invocation.Timestamp < 0) || (f && completion.Timestamp < 0) ||
                (q && admission.ObjectId != representative.ObjectId) || (f && completion.ObjectId != representative.ObjectId) ||
                (q && s && invocation.Timestamp < admission.Timestamp) || (s && f && completion.Timestamp < invocation.Timestamp) ||
                (q && f && completion.Timestamp < admission.Timestamp) ||
                (f && completion.Data0 == DispatcherEvent.AbortedCode && (s || completion.Data1 != (long)DispatcherOutcome.Aborted)) ||
                (f && completion.Data0 == DispatcherEvent.FinishedCode && completion.Data1 is not ((long)DispatcherOutcome.Returned) and not ((long)DispatcherOutcome.Threw)) ||
                (s && f && (completion.ThreadId != invocation.ThreadId || completion.NameId != invocation.NameId));
            var waitEnd = s ? invocation.Timestamp : f && completion.Data0 == DispatcherEvent.AbortedCode ? completion.Timestamp : (long?)null;
            invalid |= transitions.Any(t => !DispatcherEvent.IsValidPriority(t.Data1) || t.ObjectId != representative.ObjectId || t.Timestamp < 0 ||
                (q && t.Timestamp < admission.Timestamp) || (waitEnd is { } end && t.Timestamp > end));
            if (invalid) status = "Conflicting / invalid markers";
            else
            {
                if (q && s) wait = trace.Milliseconds(invocation.Timestamp - admission.Timestamp);
                if (s && f) run = trace.Milliseconds(completion.Timestamp - invocation.Timestamp);
                if (q && f && completion.Data0 == DispatcherEvent.AbortedCode)
                { wait = trace.Milliseconds(completion.Timestamp - admission.Timestamp); status = "Aborted before execution"; }
                if (q && waitEnd is { } until && trace.DroppedEvents == 0 && trace.MissingBlocks == 0 && trace.EvictedEvents == 0 && trace.ProbeFaults == 0 && trace.MetadataDrops == 0)
                {
                    var cursor = admission.Timestamp; var runnable = DispatcherEvent.IsRunnable(admission.Data1); long readyTicks = 0;
                    foreach (var transition in transitions)
                    {
                        if (runnable) readyTicks += transition.Timestamp - cursor;
                        cursor = transition.Timestamp; runnable = DispatcherEvent.IsRunnable(transition.Data1);
                    }
                    if (runnable) readyTicks += until - cursor;
                    ready = trace.Milliseconds(readyTicks); inactive = trace.Milliseconds(until - admission.Timestamp - readyTicks);
                }
                if (q && s && f) status = completion.Data1 == (long)DispatcherOutcome.Threw ? "Callback threw" : "Returned";
            }
            result.Add(new(id, trace.Name(representative), q ? admission : null, s ? invocation : null, f ? completion : null, status, wait, run)
            { RunnableQueueMs = ready, InactiveQueueMs = inactive, PriorityChanges = transitions });
        }
        return result.OrderByDescending(o => o.QueueMs ?? -1).ThenBy(o => o.Id).ToArray();
    }
}
