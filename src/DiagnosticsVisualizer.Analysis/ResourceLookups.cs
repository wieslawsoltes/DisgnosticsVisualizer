using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record ResourceLookup(long Id, long ParentLookupId, string Name, string Key, string Theme,
    long ProviderId, string Provider, long KeyId, long ValueId, string ResultType, string Status, double? DurationMs,
    TraceEvent? Request, TraceEvent? Completion, bool CaptureIncomplete)
{
    public long EventId => Completion?.Id ?? Request?.Id ?? 0;
    public long Timestamp => Request?.Timestamp ?? Completion?.Timestamp ?? 0;
    public bool IsComplete => Status is "Found" or "Found null" or "Missing" or "Threw";
}

/// <summary>Indexed observed calls, not a simulation of resource precedence or hypothetical providers.</summary>
public sealed class ResourceLookupIndex
{
    private sealed class Pending
    {
        public TraceEvent? Request, Theme, Completion;
        public bool Invalid;
        public void Add(TraceEvent e)
        {
            if (!ResourceLookupEvent.IsValid(e)) { Invalid = true; return; }
            if (ResourceLookupEvent.IsRequest(e)) { if (Request is not null) Invalid = true; Request = e; }
            else if (ResourceLookupEvent.IsTheme(e)) { if (Theme is not null) Invalid = true; Theme = e; }
            else { if (Completion is not null) Invalid = true; Completion = e; }
        }
    }
    private readonly Dictionary<long, ResourceLookup> _byId;
    private readonly Dictionary<long, ResourceLookup[]> _children;
    private readonly Dictionary<long, long> _rootIds = [];
    public ResourceLookup[] Calls { get; }
    public ResourceLookup[] Roots { get; }
    public int InvalidMarkers { get; }
    public ResourceLookupIndex(TraceView trace)
    {
        var pending = new Dictionary<long, Pending>(); var invalid = 0;
        foreach (var e in trace.Events)
        {
            if (!ResourceLookupEvent.IsEvent(e)) continue;
            var id = ResourceLookupEvent.LookupId(e);
            if (id <= 0) { invalid++; continue; }
            if (!pending.TryGetValue(id, out var row)) pending[id] = row = new();
            row.Add(e);
        }
        var loss = trace.DroppedEvents != 0 || trace.MissingBlocks != 0 || trace.MetadataDrops != 0 || trace.EvictedEvents != 0;
        Calls = pending.Select(pair => Build(pair.Key, pair.Value)).OrderBy(r => r.Timestamp).ThenBy(r => r.Id).ToArray();
        _byId = Calls.ToDictionary(r => r.Id);
        var cycles = FindCycles(_byId);
        Calls = Calls.Select(r => cycles.Contains(r.Id) || InvalidNesting(r) ? r with { Status = "Conflicting / invalid", DurationMs = null, CaptureIncomplete = true } :
            r.ParentLookupId != 0 && !_byId.ContainsKey(r.ParentLookupId) ? r with { CaptureIncomplete = true } : r).ToArray();
        _byId = Calls.ToDictionary(r => r.Id);
        _children = Calls.Where(r => r.ParentLookupId != 0).GroupBy(r => r.ParentLookupId).ToDictionary(g => g.Key, g => g.ToArray());
        // Even malicious cyclic parentage stays inspectable; never traverse without a visited set.
        Roots = Calls.Where(r => r.ParentLookupId == 0 || !_byId.ContainsKey(r.ParentLookupId) || cycles.Contains(r.Id)).ToArray();
        foreach (var root in Roots) _rootIds[root.Id] = root.Id;
        var path = new List<long>();
        foreach (var call in Calls)
        {
            if (_rootIds.ContainsKey(call.Id)) continue;
            path.Clear(); var cursor = call.Id;
            while (!_rootIds.ContainsKey(cursor) && _byId.TryGetValue(cursor, out var item)) { path.Add(cursor); cursor = item.ParentLookupId; }
            var root = _rootIds.GetValueOrDefault(cursor, call.Id);
            foreach (var id in path) _rootIds[id] = root;
        }
        bool InvalidNesting(ResourceLookup row) => row.Completion is { } child &&
            _byId.TryGetValue(row.ParentLookupId, out var parent) && parent.Completion is { } outer &&
            (child.ThreadId != outer.ThreadId || child.Timestamp < outer.Timestamp || child.End > outer.End);
        InvalidMarkers = invalid + Calls.Count(r => r.Status == "Conflicting / invalid");
        ResourceLookup Build(long id, Pending p)
        {
            var q = p.Request; var c = p.Completion; var t = p.Theme;
            var parent = q?.Data1 ?? c?.Data1 ?? t?.Data1 ?? 0;
            var valid = !p.Invalid && parent != id;
            if (q is { } request && c is { } completion)
                valid &= request.Timestamp == completion.Timestamp && request.ThreadId == completion.ThreadId &&
                    request.RelatedId == completion.ObjectId && request.Data1 == completion.Data1;
            if (t is { } theme && q is { } key) valid &= theme.Timestamp == key.Timestamp && theme.ThreadId == key.ThreadId && theme.Data1 == key.Data1;
            var complete = valid && q is not null && c is not null && t is not null;
            var status = !valid ? "Conflicting / invalid" : !complete ? "Incomplete / not retained" : ResourceLookupEvent.Outcome(c!.Value) switch
            { ResourceLookupOutcome.Found => "Found", ResourceLookupOutcome.FoundNull => "Found null", ResourceLookupOutcome.Missing => "Missing", _ => "Threw" };
            var provider = c?.ObjectId ?? q?.RelatedId ?? 0;
            return new(id, parent, c is { } call ? trace.Name(call) : "<completion not retained>",
                q is { } requestKey ? Label(trace.Name(requestKey)) : "<key not retained>",
                t is { } requestedTheme ? Label(trace.Name(requestedTheme)) : "<theme not retained>",
                provider, Describe(provider), q?.ObjectId ?? 0, complete ? c!.Value.RelatedId : 0,
                complete && c is { RelatedId: > 0 } returned ? Describe(returned.RelatedId) : status == "Found null" ? "null" : "—",
                status, complete ? trace.Milliseconds(c!.Value.Duration) : null, q, c, loss || !complete);
        }
        string Describe(long id) => trace.Objects.TryGetValue(id, out var o) ? o.Type + " #" + id : id == 0 ? "<none / omitted>" : "object #" + id;
    }
    public ResourceLookup? RootOf(long id) => _rootIds.TryGetValue(id, out var root) ? Find(root) : null;
    public ResourceLookup? Find(long id) => _byId.GetValueOrDefault(id);
    public (ResourceLookup Call, int Depth)[] Descendants(long id, int maximum = 8192)
    {
        if (maximum is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!_byId.TryGetValue(id, out var root)) return [];
        var result = new List<(ResourceLookup, int)>(); var seen = new HashSet<long>();
        var stack = new Stack<(ResourceLookup, int)>(); stack.Push((root, 0));
        while (stack.TryPop(out var item) && result.Count < maximum)
        {
            if (!seen.Add(item.Item1.Id)) continue; result.Add(item);
            if (_children.TryGetValue(item.Item1.Id, out var children))
                for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], item.Item2 + 1));
        }
        return result.ToArray();
    }
    private static HashSet<long> FindCycles(Dictionary<long, ResourceLookup> rows)
    {
        var cycles = new HashSet<long>(); var settled = new HashSet<long>();
        var path = new List<long>(); var positions = new Dictionary<long, int>();
        foreach (var first in rows.Keys)
        {
            if (settled.Contains(first)) continue;
            path.Clear(); positions.Clear(); var id = first;
            while (rows.TryGetValue(id, out var row) && !settled.Contains(id))
            {
                if (positions.TryGetValue(id, out var start)) { for (var i = start; i < path.Count; i++) cycles.Add(path[i]); break; }
                positions[id] = path.Count; path.Add(id); id = row.ParentLookupId;
            }
            foreach (var visited in path) settled.Add(visited);
        }
        return cycles;
    }
    private static string Label(string text) => text.StartsWith("key: ", StringComparison.Ordinal) ? text[5..] : text.StartsWith("theme: ", StringComparison.Ordinal) ? text[7..] : text;
}
