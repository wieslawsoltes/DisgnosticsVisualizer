using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record StyleEvaluation(long Id, long ParentEvaluationId, StyleEvaluationKind Kind, string Name,
    long SubjectId, string Subject, long TargetId, long ContextId, long ActivatorId,
    string Status, bool? Input, bool? Changed, double? DurationMs, TraceEvent? Begin, TraceEvent? Completion, bool CaptureIncomplete)
{
    public double? StartMs { get; init; }
    public long EventId => Completion?.Id ?? Begin?.Id ?? 0;
    public long Timestamp => Begin?.Timestamp ?? Completion?.Timestamp ?? 0;
    public bool IsComplete => DurationMs is not null;
    public string InputDescription => Input is null ? "Unknown" : Kind switch
    {
        StyleEvaluationKind.Match or StyleEvaluationKind.Evaluate => Input.Value ? "Subscribe requested" : "Immediate result requested",
        StyleEvaluationKind.Signal => Input.Value ? "Incoming active signal" : "Incoming inactive signal",
        _ => "—"
    };
    public string ChangeDescription => Changed is { } changed ? changed ? "Changed" : "Unchanged" : "—";
}

/// <summary>Observed styling execution forest. Missing observations, shared frames and conditional matches are not inferred winners.</summary>
public sealed class StyleEvaluationIndex
{
    private sealed class Pending
    {
        public TraceEvent? Begin, Context, End;
        public bool Invalid;
        public void Add(TraceEvent e)
        {
            if (!StyleEvaluationEvent.IsValid(e)) { Invalid = true; return; }
            if (StyleEvaluationEvent.IsBegin(e)) { if (Begin is not null) Invalid = true; Begin = e; }
            else if (StyleEvaluationEvent.IsContext(e)) { if (Context is not null) Invalid = true; Context = e; }
            else { if (End is not null) Invalid = true; End = e; }
        }
    }
    private readonly Dictionary<long, StyleEvaluation> _byId;
    private readonly Dictionary<long, StyleEvaluation[]> _children;
    private readonly Dictionary<long, StyleEvaluation[]> _activity;
    private readonly Dictionary<long, long> _roots = [];
    public StyleEvaluation[] Calls { get; }
    public StyleEvaluation[] Roots { get; }
    public int InvalidMarkers { get; }
    public StyleEvaluationIndex(TraceView trace)
    {
        var pending = new Dictionary<long, Pending>(); var invalid = 0;
        foreach (var e in trace.Events)
        {
            if (!StyleEvaluationEvent.IsEvent(e)) continue;
            var id = StyleEvaluationEvent.EvaluationId(e);
            if (id <= 0) { invalid++; continue; }
            if (!pending.TryGetValue(id, out var row)) pending[id] = row = new();
            row.Add(e);
        }
        var loss = trace.DroppedEvents != 0 || trace.MissingBlocks != 0 || trace.MetadataDrops != 0 || trace.EvictedEvents != 0;
        Calls = pending.Select(p => Build(p.Key, p.Value)).OrderBy(r => r.Timestamp).ThenBy(r => r.Id).ToArray();
        _byId = Calls.ToDictionary(r => r.Id);
        var cycles = FindCycles();
        Calls = Calls.Select(r => cycles.Contains(r.Id) || InvalidNesting(r)
            ? r with { Status = "Conflicting / invalid", DurationMs = null, Changed = null, CaptureIncomplete = true }
            : r.ParentEvaluationId != 0 && !_byId.ContainsKey(r.ParentEvaluationId) ? r with { CaptureIncomplete = true } : r).ToArray();
        _byId = Calls.ToDictionary(r => r.Id);
        _children = Calls.Where(r => r.ParentEvaluationId != 0).GroupBy(r => r.ParentEvaluationId).ToDictionary(g => g.Key, g => g.ToArray());
        Roots = Calls.Where(r => r.ParentEvaluationId == 0 || !_byId.ContainsKey(r.ParentEvaluationId) || cycles.Contains(r.Id)).ToArray();
        foreach (var root in Roots) _roots[root.Id] = root.Id;
        var path = new List<long>();
        foreach (var row in Calls)
        {
            if (_roots.ContainsKey(row.Id)) continue;
            path.Clear(); var cursor = row.Id;
            while (!_roots.ContainsKey(cursor) && _byId.TryGetValue(cursor, out var item)) { path.Add(cursor); cursor = item.ParentEvaluationId; }
            var root = _roots.GetValueOrDefault(cursor, row.Id); foreach (var id in path) _roots[id] = root;
        }
        var activity = new Dictionary<long, List<StyleEvaluation>>();
        foreach (var row in Calls.Where(r => r.Kind is StyleEvaluationKind.Frame or StyleEvaluationKind.Signal))
            foreach (var identity in new[] { row.SubjectId, row.ContextId, row.ActivatorId }.Where(id => id > 0).Distinct())
            { if (!activity.TryGetValue(identity, out var list)) activity[identity] = list = []; list.Add(row); }
        _activity = activity.ToDictionary(p => p.Key, p => p.Value.ToArray());
        InvalidMarkers = invalid + Calls.Count(r => r.Status == "Conflicting / invalid");
        bool InvalidNesting(StyleEvaluation row) => row.Completion is { } child && _byId.TryGetValue(row.ParentEvaluationId, out var parent) && parent.Completion is { } outer &&
            (child.ThreadId != outer.ThreadId || child.Timestamp < outer.Timestamp || child.End > outer.End);
        StyleEvaluation Build(long id, Pending p)
        {
            var observations = new[] { p.Begin, p.Context, p.End }.Where(e => e.HasValue).Select(e => e!.Value).ToArray();
            var first = observations.FirstOrDefault(); var parent = first.Data1; var kind = StyleEvaluationEvent.Operation(first);
            var valid = !p.Invalid && parent != id && observations.All(e => e.Data1 == parent && e.ThreadId == first.ThreadId &&
                e.Timestamp == first.Timestamp && e.NameId == first.NameId && StyleEvaluationEvent.Operation(e) == kind);
            if (p.Begin is { } begin && p.End is { } end) valid &= begin.ObjectId == end.ObjectId;
            var complete = valid && p.Begin is not null && p.Context is not null && p.End is not null;
            var status = !valid ? "Conflicting / invalid" : !complete ? "Incomplete / not retained" : OutcomeName(StyleEvaluationEvent.Outcome(p.End!.Value));
            var subject = p.Begin?.ObjectId ?? p.End?.ObjectId ?? 0;
            return new(id, parent, kind, observations.Length == 0 ? "<invalid observation>" : trace.Name(first), subject,
                trace.Objects.TryGetValue(subject, out var info) ? info.Type + " #" + subject : "object #" + subject,
                p.Begin?.RelatedId ?? 0, p.Context?.ObjectId ?? 0, complete ? p.End!.Value.RelatedId : 0,
                status, valid && p.Begin is { } b ? StyleEvaluationEvent.Input(b) : null,
                complete && kind == StyleEvaluationKind.Frame && StyleEvaluationEvent.Outcome(p.End!.Value) != StyleEvaluationOutcome.Threw ? StyleEvaluationEvent.Changed(p.End!.Value) : null,
                complete ? trace.Milliseconds(p.End!.Value.Duration) : null, p.Begin, p.End, loss || !complete) { StartMs = observations.Length == 0 ? null : (first.Timestamp - (double)trace.OriginTimestamp) * 1000d / trace.Frequency };
        }
    }
    public static string OutcomeName(StyleEvaluationOutcome outcome) => outcome switch
    {
        StyleEvaluationOutcome.NeverThisType => "Never this type", StyleEvaluationOutcome.NeverThisInstance => "Never this instance",
        StyleEvaluationOutcome.Conditional => "Conditional (activator)", StyleEvaluationOutcome.AlwaysThisInstance => "Always this instance",
        StyleEvaluationOutcome.AlwaysThisType => "Always this type", StyleEvaluationOutcome.Inactive => "Inactive",
        StyleEvaluationOutcome.Active => "Active", StyleEvaluationOutcome.Delivered => "Signal returned", _ => "Threw"
    };
    public StyleEvaluation? Find(long id) => _byId.GetValueOrDefault(id);
    public StyleEvaluation? RootOf(long id) => _roots.TryGetValue(id, out var root) ? Find(root) : null;
    public (StyleEvaluation Call, int Depth)[] Descendants(long id, int maximum = 8192)
    {
        if (maximum is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (Find(id) is not { } root) return [];
        var result = new List<(StyleEvaluation, int)>(); var seen = new HashSet<long>();
        var stack = new Stack<(StyleEvaluation, int)>(); stack.Push((root, 0));
        while (stack.TryPop(out var item) && result.Count < maximum)
        {
            if (!seen.Add(item.Item1.Id)) continue; result.Add(item);
            if (_children.TryGetValue(item.Item1.Id, out var children))
                for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], item.Item2 + 1));
        }
        return result.ToArray();
    }
    /// <summary>Bounded merge of pre-indexed chronological identity histories, never a scan of the whole trace on selection.</summary>
    public StyleEvaluation[] RelatedActivity(StyleEvaluation row, int maximum = 8192)
    {
        if (maximum is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximum));
        var keys = new[] { row.SubjectId, row.ActivatorId, row.Kind is StyleEvaluationKind.Frame or StyleEvaluationKind.Signal ? row.ContextId : 0 };
        var lists = keys.Where(id => id > 0 && _activity.ContainsKey(id)).Distinct().Select(id => _activity[id]).ToArray();
        var positions = new int[lists.Length]; var result = new List<StyleEvaluation>(); var seen = new HashSet<long>();
        while (result.Count < maximum)
        {
            var next = -1;
            for (var i = 0; i < lists.Length; i++)
                if (positions[i] < lists[i].Length && (next < 0 || Compare(lists[i][positions[i]], lists[next][positions[next]]) < 0)) next = i;
            if (next < 0) break;
            var item = lists[next][positions[next]++]; if (seen.Add(item.Id)) result.Add(item);
        }
        return result.ToArray();
        static int Compare(StyleEvaluation a, StyleEvaluation b) => a.Timestamp != b.Timestamp ? a.Timestamp.CompareTo(b.Timestamp) : a.Id.CompareTo(b.Id);
    }
    private HashSet<long> FindCycles()
    {
        var cycles = new HashSet<long>(); var settled = new HashSet<long>();
        var path = new List<long>(); var positions = new Dictionary<long, int>();
        foreach (var first in _byId.Keys)
        {
            if (settled.Contains(first)) continue;
            path.Clear(); positions.Clear(); var id = first;
            while (_byId.TryGetValue(id, out var row) && !settled.Contains(id))
            {
                if (positions.TryGetValue(id, out var start)) { for (var i = start; i < path.Count; i++) cycles.Add(path[i]); break; }
                positions[id] = path.Count; path.Add(id); id = row.ParentEvaluationId;
            }
            foreach (var visited in path) settled.Add(visited);
        }
        return cycles;
    }
}
