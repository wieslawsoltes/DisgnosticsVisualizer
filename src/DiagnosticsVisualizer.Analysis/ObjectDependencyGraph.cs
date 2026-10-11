using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record DependencyNode(long Id, string Label, string Type, bool IsTreeNode);
public sealed record DependencyEdge(long Source, long Target, string Kind, string Detail)
{
    public long Other(long id) => Source == id ? Target : Source;
}
public sealed record DependencyNeighborhood(long Selected, DependencyNode[] Nodes, DependencyEdge[] Edges,
    int OmittedEdges, bool IncompleteSnapshot);

/// <summary>Indexes recorded object relationships. No claim of reachability, retention or causal triggering.</summary>
public sealed class ObjectDependencyGraph
{
    private readonly Dictionary<long, DependencyNode> _nodes = [];
    private readonly Dictionary<long, List<DependencyEdge>> _incident = [];
    private readonly IReadOnlyDictionary<long, ObjectInfo> _objects;
    private readonly bool _incomplete;
    public ObjectDependencyGraph(TraceView trace, SnapshotData? snapshot = null)
    {
        snapshot ??= trace.Snapshot; _objects = trace.Objects; _incomplete = snapshot.Truncated;
        var distinct = new HashSet<DependencyEdge>();
        void Add(long source, long target, string kind, string detail)
        {
            if (source <= 0 || target <= 0) return;
            var edge = new DependencyEdge(source, target, kind, detail);
            if (!distinct.Add(edge)) return;
            if (!_incident.TryGetValue(source, out var outgoing)) _incident[source] = outgoing = [];
            outgoing.Add(edge);
            if (source != target)
            {
                if (!_incident.TryGetValue(target, out var incoming)) _incident[target] = incoming = [];
                incoming.Add(edge);
            }
        }
        foreach (var node in snapshot.Nodes)
        {
            if (!_nodes.TryAdd(node.Id, new(node.Id, node.Name.Length > 0 ? node.Name : Short(node.Type), node.Type, true)))
                throw new InvalidDataException("Duplicate snapshot node identity.");
            Add(node.Id, node.VisualParent, "Visual parent", "Recorded visual tree");
            Add(node.Id, node.LogicalParent, "Logical parent", "Recorded logical tree");
            Add(node.Id, node.TemplatedParent, "Templated parent", "Recorded template ownership");
        }
        foreach (var relation in snapshot.Relations) Add(relation.Source, relation.Target, relation.Kind, relation.Detail);
    }
    public DependencyNode Node(long id) => _nodes.GetValueOrDefault(id) ??
        (_objects.TryGetValue(id, out var value) ? new(id, value.Display, value.Type, false) : new(id, $"Object #{id}", "Metadata not retained", false));
    public IReadOnlyList<DependencyEdge> Edges(long id) => _incident.TryGetValue(id, out var edges) ? edges.AsReadOnly() : Array.Empty<DependencyEdge>();
    public DependencyNeighborhood Neighborhood(long selected, int maxNodes = 17, int maxEdges = 64)
    {
        if (maxNodes is < 1 or > 257 || maxEdges is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (selected <= 0) return new(selected, [], [], 0, _incomplete);
        var ids = new HashSet<long> { selected }; var nodes = new List<DependencyNode> { Node(selected) };
        var result = new List<DependencyEdge>(); var edges = Edges(selected);
        foreach (var edge in edges)
        {
            var other = edge.Other(selected);
            if (result.Count == maxEdges) break;
            if (!ids.Contains(other))
            {
                if (ids.Count == maxNodes) continue;
                ids.Add(other); nodes.Add(Node(other));
            }
            result.Add(edge);
        }
        return new(selected, nodes.ToArray(), result.ToArray(), edges.Count - result.Count, _incomplete);
    }
    private static string Short(string type) => type[(type.LastIndexOf('.') + 1)..];
}
