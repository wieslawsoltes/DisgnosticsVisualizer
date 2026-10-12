using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record RoutedStep(TraceEvent Event, string Name, double? DurationMs)
{
    public long EventId => Event.Id;
    public long ObjectId => Event.ObjectId;
    public long SourceId => Event.RelatedId;
    public string Phase => RoutedDispatchEvent.RouteName(RoutedDispatchEvent.Route(Event));
    public string Kind => RoutedDispatchEvent.Kind(Event) == RoutedStepKind.ClassListeners ? "Class listeners" : "Instance handler";
    public bool HandledBefore => RoutedDispatchEvent.HandledBefore(Event);
    public bool HandledAfter => RoutedDispatchEvent.HandledAfter(Event);
    public bool HandledEventsToo => RoutedDispatchEvent.HandledEventsToo(Event);
    public bool ChangedHandled => HandledBefore != HandledAfter;
    public string Status => RoutedDispatchEvent.Kind(Event) switch
    {
        RoutedStepKind.SkippedHandled => "Skipped: handled",
        RoutedStepKind.SkippedRoute => "Skipped: route",
        _ => RoutedDispatchEvent.Returned(Event) ? "Returned" : "Threw"
    };
}

public sealed record RoutedDispatch(long Id, string Name, TraceEvent? Admission, TraceEvent? Completion,
    RoutedStep[] Steps, string Status, double? DurationMs, bool CaptureIncomplete)
{
    public long SourceId => Admission?.ObjectId ?? Completion?.ObjectId ?? 0;
    public long RoutedEventId => Admission?.RelatedId ?? Completion?.RelatedId ?? 0;
    public long EventId => Completion?.Id ?? Admission?.Id ?? Steps.FirstOrDefault()?.EventId ?? 0;
    public long Start => Admission?.Timestamp ?? Completion?.Timestamp ?? Steps.FirstOrDefault()?.Event.Timestamp ?? 0;
    public string Routes => RoutedDispatchEvent.RouteName(Admission is { } a ? RoutedDispatchEvent.Route(a) : Completion is { } c ? RoutedDispatchEvent.Route(c) : 0);
    public bool? HandledAfter => Completion is { } c ? RoutedDispatchEvent.HandledAfter(c) : null;
    public int Invoked => Steps.Count(s => s.Event.Kind == TraceEventKind.Span && RoutedDispatchEvent.Kind(s.Event) == RoutedStepKind.Handler);
    public int Skipped => Steps.Count(s => s.Event.Kind == TraceEventKind.Instant);
    public int ChangedHandled => Steps.Count(s => s.ChangedHandled);
}

public static class RoutedDispatches
{
    private sealed class Group
    {
        public TraceEvent? Admission, Completion;
        public bool Conflict;
        public readonly List<TraceEvent> Steps = [];
    }
    /// <summary>One grouping pass plus per-dispatch ordering. Loss never becomes proof that a handler was absent.</summary>
    public static RoutedDispatch[] Analyze(TraceView trace)
    {
        var groups = new Dictionary<long, Group>();
        foreach (var e in trace.Events)
        {
            if (!RoutedDispatchEvent.IsEvent(e)) continue;
            if (!groups.TryGetValue(e.Data1, out var group)) groups.Add(e.Data1, group = new());
            if (!RoutedDispatchEvent.IsValid(e) || e.Timestamp < 0 || e.Duration < 0) { group.Conflict = true; continue; }
            switch (RoutedDispatchEvent.Kind(e))
            {
                case RoutedStepKind.Begin: group.Conflict |= group.Admission is not null; group.Admission = e; break;
                case RoutedStepKind.Dispatch: group.Conflict |= group.Completion is not null; group.Completion = e; break;
                default: group.Steps.Add(e); break;
            }
        }
        var incomplete = trace.DroppedEvents != 0 || trace.MissingBlocks != 0 || trace.EvictedEvents != 0 || trace.MetadataDrops != 0 || trace.ProbeFaults != 0;
        var result = new List<RoutedDispatch>(groups.Count);
        foreach (var (id, group) in groups)
        {
            var start = group.Admission; var end = group.Completion;
            var steps = group.Steps.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToArray();
            if (start is { } a && end is { } c)
                group.Conflict |= a.Timestamp != c.Timestamp || a.ObjectId != c.ObjectId || a.RelatedId != c.RelatedId || a.NameId != c.NameId ||
                    a.ThreadId != c.ThreadId || RoutedDispatchEvent.Route(a) != RoutedDispatchEvent.Route(c) || RoutedDispatchEvent.HandledBefore(a) != RoutedDispatchEvent.HandledBefore(c);
            if (end is { } completed)
                group.Conflict |= steps.Any(e => e.ThreadId != completed.ThreadId || e.Timestamp < completed.Timestamp || e.End > completed.End);
            var representative = start ?? end ?? steps.FirstOrDefault();
            var status = group.Conflict ? "Conflicting / invalid" : start is null || end is null ? "Incomplete / not retained" :
                RoutedDispatchEvent.Returned(end.Value) ? "Returned" : "Threw";
            result.Add(new(id, trace.Name(representative), start, end,
                steps.Select(e => new RoutedStep(e, trace.Name(e), e.Kind == TraceEventKind.Span ? trace.Milliseconds(e.Duration) : null)).ToArray(),
                status, !group.Conflict && end is { } measured ? trace.Milliseconds(measured.Duration) : null, incomplete));
        }
        return result.OrderByDescending(r => r.Start).ThenByDescending(r => r.Id).ToArray();
    }
}
