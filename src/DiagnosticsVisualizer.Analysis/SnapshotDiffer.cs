using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

[Flags]
public enum NodeChangeKind
{
    None = 0, Added = 1, Removed = 2, Geometry = 4, VisualParent = 8,
    LogicalParent = 16, TemplatedParent = 32, Visibility = 64, Classes = 128,
    Name = 256, Type = 512, UnknownPresence = 1024
}
public sealed record NodeChange(long ObjectId, NodeChangeKind Kind, NodeSnapshot? Before, NodeSnapshot? After);
public sealed record PropertyChange(long ObjectId, string Name, string Kind, PropertySnapshot? Before, PropertySnapshot? After);
public sealed record SnapshotDifference(NodeChange[] Nodes, PropertyChange[] Properties, bool Incomplete);

/// <summary>
/// Linear-time tree/property comparison within a source session. Missing nodes in a
/// truncated checkpoint are unknown, not removals. Property inspection is selected-object scoped.
/// </summary>
public static class SnapshotDiffer
{
    public static SnapshotDifference Compare(Guid sessionBefore, SnapshotData before, Guid sessionAfter, SnapshotData after)
    {
        if (sessionBefore == Guid.Empty || sessionBefore != sessionAfter)
            throw new ArgumentException("Object identities can only be compared within the same nonempty source session.");
        var previous = IndexNodes(before.Nodes);
        var next = IndexNodes(after.Nodes);
        var changes = new List<NodeChange>();
        foreach (var (id, node) in next)
        {
            if (!previous.TryGetValue(id, out var old))
            {
                changes.Add(new(id, before.Truncated ? NodeChangeKind.UnknownPresence : NodeChangeKind.Added, null, node));
                continue;
            }
            var kind = NodeChangeKind.None;
            if (old.X != node.X || old.Y != node.Y || old.Width != node.Width || old.Height != node.Height) kind |= NodeChangeKind.Geometry;
            if (old.VisualParent != node.VisualParent) kind |= NodeChangeKind.VisualParent;
            if (old.LogicalParent != node.LogicalParent) kind |= NodeChangeKind.LogicalParent;
            if (old.TemplatedParent != node.TemplatedParent) kind |= NodeChangeKind.TemplatedParent;
            if (old.Visible != node.Visible) kind |= NodeChangeKind.Visibility;
            if (old.Classes != node.Classes) kind |= NodeChangeKind.Classes;
            if (old.Name != node.Name) kind |= NodeChangeKind.Name;
            if (old.Type != node.Type) kind |= NodeChangeKind.Type;
            if (kind != NodeChangeKind.None) changes.Add(new(id, kind, old, node));
        }
        foreach (var (id, old) in previous)
            if (!next.ContainsKey(id)) changes.Add(new(id, after.Truncated ? NodeChangeKind.UnknownPresence : NodeChangeKind.Removed, old, null));

        var properties = new List<PropertyChange>();
        // A selection change is not a deletion of the previously inspected object's properties.
        if (before.SelectedObject != 0 && before.SelectedObject == after.SelectedObject)
        {
            var oldProperties = IndexProperties(before.Properties);
            var newProperties = IndexProperties(after.Properties);
            foreach (var (key, value) in newProperties)
            {
                oldProperties.TryGetValue(key, out var old);
                if (old != value) properties.Add(new(value.ObjectId, value.Name, value.Kind, old, value));
            }
            if (!after.Truncated)
                foreach (var (key, old) in oldProperties)
                    if (!newProperties.ContainsKey(key)) properties.Add(new(old.ObjectId, old.Name, old.Kind, old, null));
        }
        return new(changes.ToArray(), properties.ToArray(), before.Truncated || after.Truncated);
    }
    private static Dictionary<long, NodeSnapshot> IndexNodes(NodeSnapshot[] nodes)
    {
        var result = new Dictionary<long, NodeSnapshot>(nodes.Length);
        foreach (var node in nodes)
            if (!result.TryAdd(node.Id, node)) throw new InvalidDataException("Duplicate node identity in snapshot.");
        return result;
    }
    private static Dictionary<(long, string, string, string, int), PropertySnapshot> IndexProperties(PropertySnapshot[] properties)
    {
        var result = new Dictionary<(long, string, string, string, int), PropertySnapshot>();
        var occurrences = new Dictionary<(long, string, string, string), int>();
        foreach (var property in properties)
        {
            var key = (property.ObjectId, property.Name, property.Kind, property.Source);
            var ordinal = occurrences.GetValueOrDefault(key);
            occurrences[key] = ordinal + 1;
            result.Add((key.ObjectId, key.Name, key.Kind, key.Source, ordinal), property);
        }
        return result;
    }
}
