using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record PropertyOperation(long Id, PropertyOperationKind Kind, string Name, string Property,
    TraceEvent? Begin, TraceEvent? Context, TraceEvent? Before, TraceEvent? After, TraceEvent? End,
    long ParentOperationId, bool Invalid, bool CaptureIncomplete, double? DurationMs)
{
    public long TargetId => Begin?.ObjectId ?? End?.ObjectId ?? 0;
    public long PropertyId => Begin?.RelatedId ?? End?.RelatedId ?? 0;
    public long ParticipantId => Context?.ObjectId ?? 0;
    public long Timestamp => Begin?.Timestamp ?? End?.Timestamp ?? Context?.Timestamp ?? Before?.Timestamp ?? After?.Timestamp ?? 0;
    public long EventId => End?.Id ?? Begin?.Id ?? Context?.Id ?? Before?.Id ?? After?.Id ?? 0;
    public bool IsComplete => !Invalid && Begin is not null && Context is not null && Before is not null && After is not null && End is not null;
    public PropertyOperationOutcome? Outcome => End is { } e && !Invalid ? PropertyOperationEvent.Outcome(e) : null;
    public int Flags => !Invalid && (Begin ?? End) is { } e ? PropertyOperationEvent.Flags(e) : 0;
    public bool Effective => Kind == PropertyOperationKind.Notify && (Flags & 8) != 0;
    public bool BaseValue => Kind == PropertyOperationKind.Notify && (Flags & 16) != 0;
    public string Category => Effective ? "Effective notification" : BaseValue ? "Base notification" : Kind.ToString();
    public string PropertyKind => Invalid || (Begin ?? End) is null ? "Unknown" : (Flags & 1) != 0 ? "Direct" : (Flags & 2) != 0 ? "Attached" : "Styled";
    public PropertyValue OldValue => !Invalid && Before is { } e ? PropertyOperationEvent.Value(e) : default;
    public PropertyValue NewValue => !Invalid && After is { } e ? PropertyOperationEvent.Value(e) : default;
    public string BeforeText => OldValue.ToString();
    public string AfterText => NewValue.ToString();
    public long Priority => !Invalid ? Context?.Data1 ?? PropertyOperationEvent.UnknownPriority : PropertyOperationEvent.UnknownPriority;
    public string PriorityText => PropertyOperationEvent.PriorityName(Priority);
    public string Status => Invalid ? "Invalid markers" : !IsComplete ? "Incomplete" : Outcome switch
    { PropertyOperationOutcome.Returned => "Returned", PropertyOperationOutcome.Changed => "Changed", PropertyOperationOutcome.Unchanged => "Unchanged", _ => "Threw" };
}

/// <summary>Observed setter/notification histories. No values, precedence winners or state between observations are synthesized.</summary>
public sealed class PropertyOperationIndex
{
    private sealed class Parts { public readonly TraceEvent?[] Values = new TraceEvent?[5]; public bool Invalid; }
    private readonly Dictionary<long, PropertyOperation> _byId;
    private readonly Dictionary<(long Target, long Property), PropertyOperation[]> _history;
    private readonly Dictionary<long, PropertyOperation[]> _children;
    public PropertyOperation[] Calls { get; }
    public int InvalidMarkers { get; }
    public PropertyOperationIndex(TraceView trace)
    {
        var parts = new Dictionary<long, Parts>(); var malformed = 0;
        foreach (var e in trace.Events)
        {
            if (!PropertyOperationEvent.IsEvent(e)) continue;
            var id = PropertyOperationEvent.OperationId(e);
            if (id <= 0) { malformed++; continue; }
            if (!parts.TryGetValue(id, out var p)) parts[id] = p = new();
            if (!PropertyOperationEvent.IsValid(e)) { p.Invalid = true; continue; }
            var phase = PropertyOperationEvent.Phase(e) - 1;
            if (p.Values[phase] is not null) p.Invalid = true; else p.Values[phase] = e;
        }
        var loss = trace.DroppedEvents != 0 || trace.MetadataDrops != 0 || trace.MissingBlocks != 0 || trace.EvictedEvents != 0;
        var rows = new List<PropertyOperation>();
        foreach (var (id, p) in parts)
        {
            var b = p.Values[0]; var c = p.Values[1]; var old = p.Values[2]; var value = p.Values[3]; var e = p.Values[4];
            if ((b ?? e ?? c ?? old ?? value) is not { } first) { malformed++; continue; }
            var kind = PropertyOperationEvent.Operation(first); var parent = b?.Data1 ?? e?.Data1 ?? 0;
            var invalid = p.Invalid || parent == id || p.Values.OfType<TraceEvent>().Any(v => v.Timestamp != first.Timestamp ||
                v.ThreadId != first.ThreadId || v.NameId != first.NameId || PropertyOperationEvent.Operation(v) != kind);
            if (b is { } begin && e is { } end)
                invalid |= begin.ObjectId != end.ObjectId || begin.RelatedId != end.RelatedId || begin.Data1 != end.Data1 || PropertyOperationEvent.Flags(begin) != PropertyOperationEvent.Flags(end);
            var complete = !invalid && p.Values.All(v => v is not null);
            var propertyId = b?.RelatedId ?? e?.RelatedId ?? 0;
            var name = trace.Objects.TryGetValue(propertyId, out var info) ? info.Display : $"property #{propertyId}";
            rows.Add(new(id, kind, trace.Name(first), name, b, c, old, value, e, parent, invalid, loss || !complete, complete ? trace.Milliseconds(e!.Value.Duration) : null));
        }
        _byId = rows.ToDictionary(r => r.Id);
        var bad = new HashSet<long>(); var visited = new HashSet<long>(); var path = new List<long>(); var positions = new Dictionary<long, int>();
        foreach (var row in rows)
        {
            if (visited.Contains(row.Id)) continue;
            path.Clear(); positions.Clear(); var cursor = row.Id;
            while (_byId.TryGetValue(cursor, out var current) && !visited.Contains(cursor))
            {
                if (positions.TryGetValue(cursor, out var cycle)) { for (var i = cycle; i < path.Count; i++) bad.Add(path[i]); break; }
                positions[cursor] = path.Count; path.Add(cursor); cursor = current.ParentOperationId;
            }
            foreach (var id in path) visited.Add(id);
        }
        foreach (var row in rows)
            if (row.End is { } child && _byId.TryGetValue(row.ParentOperationId, out var parent) && parent.End is { } outer &&
                (child.Timestamp < outer.Timestamp || child.End > outer.End || child.ThreadId != outer.ThreadId)) bad.Add(row.Id);
        Calls = rows.Select(r => bad.Contains(r.Id) ? r with { Invalid = true, CaptureIncomplete = true, DurationMs = null } :
            r.ParentOperationId > 0 && !_byId.ContainsKey(r.ParentOperationId) ? r with { CaptureIncomplete = true } : r).OrderBy(r => r.Timestamp).ThenBy(r => r.Id).ToArray();
        _byId = Calls.ToDictionary(r => r.Id);
        _history = Calls.GroupBy(r => (r.TargetId, r.PropertyId)).ToDictionary(g => g.Key, g => g.ToArray());
        _children = Calls.Where(r => r.ParentOperationId > 0).GroupBy(r => r.ParentOperationId).ToDictionary(g => g.Key, g => g.ToArray());
        InvalidMarkers = malformed + Calls.Count(r => r.Invalid);
    }
    public PropertyOperation? Find(long id) => _byId.GetValueOrDefault(id);
    public PropertyOperation[] History(long target, long property, int maximum = 8192)
    {
        Limit(maximum); return _history.TryGetValue((target, property), out var rows) ? rows.Take(maximum).ToArray() : [];
    }
    public PropertyOperation[] Descendants(long id, int maximum = 8192)
    {
        Limit(maximum); if (Find(id) is not { } root) return [];
        var result = new List<PropertyOperation>(); var pending = new Stack<PropertyOperation>(); var seen = new HashSet<long>(); pending.Push(root);
        while (pending.TryPop(out var row) && result.Count < maximum)
        {
            if (!seen.Add(row.Id)) continue; result.Add(row);
            if (_children.TryGetValue(row.Id, out var children)) for (var n = children.Length - 1; n >= 0; n--) pending.Push(children[n]);
        }
        return result.ToArray();
    }
    private static void Limit(int maximum) { if (maximum is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximum)); }
}
