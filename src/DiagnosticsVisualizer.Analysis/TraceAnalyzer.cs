using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record Finding(string Severity, string Title, string Explanation, long EventId,
    long ObjectId, bool Inferred = false);
public sealed record CostRow(string Name, Subsystem Subsystem, int Count, double TotalMs,
    double SelfMs, double MaximumMs);
public sealed record CausalEdge(long Source, long Target, string Kind, bool Inferred = false);
public sealed record AnalysisResult(CostRow[] Costs, Finding[] Findings, CausalEdge[] Edges)
{
    public DispatcherOperation[] DispatcherOperations { get; init; } = [];
    public AsyncOperation[] AsyncOperations { get; init; } = [];
}

public static class TraceAnalyzer
{
    public static AnalysisResult Analyze(TraceView trace, double frameBudgetMs = 1000d / 60)
    {
        if (!double.IsFinite(frameBudgetMs) || frameBudgetMs <= 0 || frameBudgetMs > 60_000)
            throw new ArgumentOutOfRangeException(nameof(frameBudgetMs));
        var findings = new List<Finding>(); var edges = new List<CausalEdge>();
        var byId = trace.Events.ToDictionary(x => x.Id);
        var children = new Dictionary<long, List<TraceEvent>>();
        foreach (var e in trace.Events)
        {
            if (e.ParentId != 0 && byId.TryGetValue(e.ParentId, out var parent))
            {
                if (!children.TryGetValue(e.ParentId, out var list)) children[e.ParentId] = list = [];
                list.Add(e);
                var kind = DispatcherEvent.IsMarker(e) ? "dispatcher lifecycle" : DispatcherEvent.IsMarker(parent) && parent.Data0 == DispatcherEvent.StartedCode ? "dispatcher callback" : AsyncOperationEvent.IsBegin(parent) && AsyncOperationEvent.IsEnd(e) ? "async completion" : parent.ThreadId != e.ThreadId ? "cross-thread causal parent" :
                    e.Timestamp < parent.Timestamp || e.End > parent.End ? "continuation parent" : "synchronous parent";
                edges.Add(new(e.ParentId, e.Id, kind));
            }
        }
        var costs = trace.Events.Where(x => x.Kind == TraceEventKind.Span)
            .GroupBy(e => (e.NameId, e.Subsystem)).Select(group =>
            {
                double total = 0, self = 0, max = 0; var count = 0;
                foreach (var e in group)
                {
                    total += e.Duration; max = Math.Max(max, e.Duration); count++;
                    self += e.Duration - CoveredDuration(e, children.GetValueOrDefault(e.Id));
                }
                return new CostRow(trace.Name(group.First()), group.Key.Subsystem, count,
                    total * 1000 / trace.Frequency, self * 1000 / trace.Frequency, max * 1000 / trace.Frequency);
            }).OrderByDescending(x => x.SelfMs).ToArray();
        foreach (var e in trace.Events)
        {
            if (e.Kind == TraceEventKind.Span && trace.Milliseconds(e.Duration) > frameBudgetMs &&
                e.Subsystem is Subsystem.Layout or Subsystem.Rendering or Subsystem.Dispatcher)
                findings.Add(new("Warning", $"{e.Subsystem} operation exceeded frame budget",
                    $"{trace.Name(e)} took {trace.Milliseconds(e.Duration):F2} ms; budget {frameBudgetMs:F2} ms. Inclusive duration, not GPU time.", e.Id, e.ObjectId));
        }
        // A windowed count is a symptom, not proof of a cycle or a root cause.
        var window = Math.Max(1, (long)(trace.Frequency * frameBudgetMs / 1000));
        foreach (var group in trace.Events.Where(e => e.ObjectId != 0 &&
            e.Subsystem is Subsystem.Layout or Subsystem.Properties or Subsystem.PropertyNotifications)
            .GroupBy(e => (e.ObjectId, e.Subsystem, Window: (e.Timestamp - trace.OriginTimestamp) / window)))
        {
            var count = group.Count(); var threshold = group.Key.Subsystem == Subsystem.Layout ? 32 : 128;
            if (count < threshold) continue;
            var first = group.First();
            findings.Add(new("Warning", $"Repeated {group.Key.Subsystem} activity",
                $"{count} recorded operations for object #{first.ObjectId} in a {frameBudgetMs:F2} ms bucket. Inspect invalidation and parent spans before attributing a cause.",
                first.Id, first.ObjectId, true));
        }
        var dispatcher = global::DiagnosticsVisualizer.Analysis.DispatcherOperations.Analyze(trace);
        foreach (var operation in dispatcher.Where(o => o.Started is not null && o.RunnableQueueMs > frameBudgetMs))
            findings.Add(new("Warning", "Measured dispatcher queue delay",
                $"Operation #{operation.ObjectId} waited {operation.RunnableQueueMs:F2} ms at runnable priority ({operation.QueueMs:F2} ms total residency); posted priority {operation.PostedPriority}, execution priority {operation.ExecutionPriority}. This is latency, not CPU time.", operation.EventId, operation.ObjectId));
        var queued = new Dictionary<long, TraceEvent>();
        foreach (var e in trace.Events.Where(e => dispatcher.Length == 0 && e.Subsystem == Subsystem.Dispatcher))
        {
            var name = trace.Name(e);
            if (e.RelatedId != 0 && (name.Contains("Enqueue", StringComparison.Ordinal) || name.Contains("AddToQueue", StringComparison.Ordinal)))
                queued[e.RelatedId] = e;
            if (e.ObjectId != 0 && name.Contains("Invoke", StringComparison.Ordinal) && queued.Remove(e.ObjectId, out var producer) && producer.Timestamp <= e.Timestamp)
            {
                edges.Add(new(producer.Id, e.Id, "inferred dispatcher operation identity", true));
                var delay = trace.Milliseconds(Math.Max(0, e.Timestamp - producer.End));
                if (delay > frameBudgetMs) findings.Add(new("Warning", "Dispatcher queue delay",
                    $"Operation #{e.ObjectId} waited approximately {delay:F2} ms after enqueue returned.", e.Id, e.ObjectId, true));
            }
        }
        if (trace.DroppedEvents + trace.MissingBlocks + trace.MetadataDrops > 0)
            findings.Insert(0, new("Warning", "Capture is incomplete",
                $"{trace.DroppedEvents} dropped events, {trace.MissingBlocks} missing batches, {trace.MetadataDrops} metadata drops. Absence of an event is not evidence it did not happen.", 0, 0));
        if (trace.ProbeFaults > 0) findings.Insert(0, new("Error", "Instrumentation faults", $"{trace.ProbeFaults} probe faults were contained. Review the weaving manifest and compatibility report.", 0, 0));
        return new(costs, findings.Take(2000).ToArray(), edges.ToArray()) { DispatcherOperations = dispatcher, AsyncOperations = global::DiagnosticsVisualizer.Analysis.AsyncOperations.Analyze(trace) };
    }
    private static long CoveredDuration(TraceEvent parent, List<TraceEvent>? children)
    {
        if (children is null) return 0;
        long covered = 0, start = 0, end = 0; var active = false;
        foreach (var child in children.Where(x => x.ThreadId == parent.ThreadId && x.Duration > 0).OrderBy(x => x.Timestamp))
        {
            var a = Math.Max(parent.Timestamp, child.Timestamp); var b = Math.Min(parent.End, child.End);
            if (b <= a) continue;
            if (!active) { start = a; end = b; active = true; }
            else if (a <= end) end = Math.Max(end, b);
            else { covered += end - start; start = a; end = b; }
        }
        if (active) covered += end - start;
        return Math.Min(parent.Duration, covered);
    }
}
