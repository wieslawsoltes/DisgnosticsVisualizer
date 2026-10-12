using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record BindingOperation(long Id, BindingOperationKind Kind, string Name, TraceEvent? Begin,
    TraceEvent? Context, TraceEvent? Input, TraceEvent? End, long ParentOperationId, bool Invalid, bool CaptureIncomplete, double? DurationMs)
{
    public long ExpressionId => Begin?.ObjectId ?? End?.ObjectId ?? 0;
    public long TargetId => Begin?.RelatedId ?? 0;
    public long PropertyId => Context?.ObjectId ?? 0;
    public long ParticipantId => Context?.RelatedId ?? 0;
    public long InputId => Input?.ObjectId ?? 0;
    public long OutputId => End?.RelatedId ?? 0;
    public long Timestamp => Begin?.Timestamp ?? End?.Timestamp ?? Context?.Timestamp ?? Input?.Timestamp ?? 0;
    public long EventId => End?.Id ?? Begin?.Id ?? Context?.Id ?? Input?.Id ?? 0;
    public bool IsComplete => !Invalid && Begin is not null && Context is not null && Input is not null && End is not null;
    public BindingOperationOutcome? Outcome => End is { } end && !Invalid ? BindingOperationEvent.Outcome(end) : null;
    public string Status => Invalid ? "Invalid markers" : !IsComplete ? "Incomplete" : Outcome switch
    { BindingOperationOutcome.Returned => "Returned", BindingOperationOutcome.True => "Returned true", BindingOperationOutcome.False => "Returned false", _ => "Threw" };
    public string InputKind => Begin is { } e ? BindingOperationEvent.ValueKind(e.Data0).ToString() : "Unknown";
    public string OutputKind => End is { } e && !Invalid ? BindingOperationEvent.ValueKind(e.Data0).ToString() : "Unknown";
    public string Running => $"{Flag(Begin is { } b ? BindingOperationEvent.Running(b.Data0) : null)} → {Flag(End is { } e && !Invalid ? BindingOperationEvent.Running(e.Data0) : null)}";
    public string Error => End is { } e && !Invalid ? ErrorText(BindingOperationEvent.Error(e.Data0)) : "Unknown";
    public string Changes => End is { } e && !Invalid ? $"value {Flag(BindingOperationEvent.ValueChanged(e.Data0))}; error {Flag(BindingOperationEvent.ErrorChanged(e.Data0))}" : "Unknown";
    public string Detail => Input is { Data1: not long.MinValue } input ? input.Data1.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
    private static string Flag(bool? value) => value is null ? "?" : value.Value ? "true" : "false";
    private static string ErrorText(int? error) => error switch { 0 => "None", 1 => "Binding error", 2 => "Validation error", _ => "Unknown" };
}

/// <summary>Observed call forest and expression history. No inferred subscription state or effective-value winner.</summary>
public sealed class BindingOperationIndex
{
    private sealed class Parts
    {
        public readonly TraceEvent?[] Phases = new TraceEvent?[4];
        public bool Invalid;
    }
    private readonly Dictionary<long, BindingOperation> _byId;
    private readonly Dictionary<long, BindingOperation[]> _children;
    private readonly Dictionary<long, BindingOperation[]> _expressions;
    public BindingOperation[] Calls { get; }
    public BindingOperation[] Roots { get; }
    public int InvalidMarkers { get; }
    public BindingOperationIndex(TraceView trace)
    {
        var parts = new Dictionary<long, Parts>(); var malformed = 0;
        foreach (var e in trace.Events)
        {
            if (!BindingOperationEvent.IsEvent(e)) continue;
            var id = BindingOperationEvent.OperationId(e);
            if (id <= 0) { malformed++; continue; }
            if (!parts.TryGetValue(id, out var row)) parts.Add(id, row = new());
            if (!BindingOperationEvent.IsValid(e)) { row.Invalid = true; continue; }
            var phase = BindingOperationEvent.Phase(e) - 1;
            if (row.Phases[phase] is not null) row.Invalid = true;
            else row.Phases[phase] = e;
        }
        var rows = new List<BindingOperation>(parts.Count);
        var loss = trace.DroppedEvents != 0 || trace.MetadataDrops != 0 || trace.MissingBlocks != 0 || trace.EvictedEvents != 0;
        foreach (var (id, p) in parts)
        {
            var begin = p.Phases[0]; var context = p.Phases[1]; var input = p.Phases[2]; var end = p.Phases[3];
            var sample = begin ?? end ?? context ?? input;
            if (sample is null) { malformed++; continue; }
            var first = sample.Value; var kind = BindingOperationEvent.Operation(first);
            var parent = begin?.Data1 ?? end?.Data1 ?? context?.Data1 ?? 0;
            foreach (var phase in p.Phases.OfType<TraceEvent>())
            {
                if (BindingOperationEvent.Operation(phase) != kind || phase.ThreadId != first.ThreadId ||
                    phase.NameId != first.NameId || phase.Timestamp != first.Timestamp ||
                    BindingOperationEvent.Phase(phase) != 3 && phase.Data1 != parent) p.Invalid = true;
            }
            if (begin is { } b && end is { } e && (b.ObjectId != e.ObjectId || e.Duration > 0 && e.End <= e.Timestamp)) p.Invalid = true;
            var complete = p.Phases.All(e => e is not null);
            rows.Add(new(id, kind, trace.Name(first), begin, context, input, end, parent, p.Invalid, loss || !complete,
                complete && !p.Invalid && end is { } completed ? trace.Milliseconds(completed.Duration) : null));
        }
        _byId = rows.ToDictionary(r => r.Id);
        // Functional-parent graph: visit each node once; never walk an unbounded cycle per row.
        var done = new HashSet<long>(); var invalid = new HashSet<long>();
        foreach (var row in rows)
        {
            var path = new List<long>(); var position = new Dictionary<long, int>(); var current = row;
            while (!done.Contains(current.Id))
            {
                if (position.TryGetValue(current.Id, out var cycle)) { for (var i = cycle; i < path.Count; i++) invalid.Add(path[i]); break; }
                position.Add(current.Id, path.Count); path.Add(current.Id);
                if (!_byId.TryGetValue(current.ParentOperationId, out current!)) break;
            }
            foreach (var id in path) done.Add(id);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var bad = invalid.Contains(row.Id);
            var orphan = row.ParentOperationId != 0 && !_byId.ContainsKey(row.ParentOperationId);
            if (!bad && row.IsComplete && _byId.TryGetValue(row.ParentOperationId, out var parent) && parent.IsComplete)
                bad = row.End!.Value.ThreadId != parent.End!.Value.ThreadId || row.Timestamp < parent.Timestamp || row.End.Value.End > parent.End.Value.End;
            rows[i] = row with { Invalid = row.Invalid || bad, CaptureIncomplete = row.CaptureIncomplete || orphan || bad, DurationMs = bad ? null : row.DurationMs };
            _byId[row.Id] = rows[i];
        }
        Calls = rows.OrderBy(r => r.Timestamp).ThenBy(r => r.Id).ToArray();
        Roots = Calls.Where(r => r.ParentOperationId == 0 || !_byId.ContainsKey(r.ParentOperationId)).ToArray();
        _children = Calls.Where(r => r.ParentOperationId != 0).GroupBy(r => r.ParentOperationId).ToDictionary(g => g.Key, g => g.ToArray());
        _expressions = Calls.Where(r => r.ExpressionId != 0).GroupBy(r => r.ExpressionId).ToDictionary(g => g.Key, g => g.ToArray());
        InvalidMarkers = malformed + Calls.Count(r => r.Invalid);
    }
    public BindingOperation? Find(long id) => _byId.GetValueOrDefault(id);
    public BindingOperation[] History(long expression, int limit = 8192)
    {
        ValidateLimit(limit); var rows = _expressions.GetValueOrDefault(expression) ?? [];
        return rows.Length <= limit ? rows.ToArray() : rows[^limit..];
    }
    public BindingOperation[] Descendants(long id, int limit = 8192)
    {
        ValidateLimit(limit); if (!_byId.TryGetValue(id, out var root)) return [];
        var result = new List<BindingOperation>(); var stack = new Stack<BindingOperation>(); var visited = new HashSet<long>(); stack.Push(root);
        while (stack.Count > 0 && result.Count < limit)
        {
            var row = stack.Pop(); if (!visited.Add(row.Id)) continue; result.Add(row);
            if (_children.TryGetValue(row.Id, out var children)) for (var i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
        }
        return result.ToArray();
    }
    public long RecordedAttachmentTarget(BindingOperation operation)
    {
        if (operation.TargetId > 0) return operation.TargetId;
        if (!_expressions.TryGetValue(operation.ExpressionId, out var history)) return 0;
        // A historical successful attachment is labelled as such by the UI; it is not a current subscription claim.
        for (var i = history.Length - 1; i >= 0; i--)
        {
            var row = history[i];
            if (row.Kind == BindingOperationKind.Attach && row.IsComplete && row.Outcome == BindingOperationOutcome.Returned && row.End!.Value.End <= operation.Timestamp) return row.TargetId;
        }
        return 0;
    }
    private static void ValidateLimit(int limit) { if (limit is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(limit)); }
}
