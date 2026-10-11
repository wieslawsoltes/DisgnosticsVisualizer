namespace DiagnosticsVisualizer.Core;

/// <summary>Cold-path metadata closure. Event references precede snapshot-only relationships.</summary>
public static class TraceObjectReferences
{
    public static IEnumerable<long> Enumerate(IEnumerable<TraceEvent> events, SnapshotData? snapshot)
    {
        var seen = new HashSet<long>();
        foreach (var e in events)
        {
            if (e.ObjectId > 0 && seen.Add(e.ObjectId)) yield return e.ObjectId;
            if (e.RelatedId > 0 && seen.Add(e.RelatedId)) yield return e.RelatedId;
        }
        if (snapshot is null) yield break;
        if (snapshot.SelectedObject > 0 && seen.Add(snapshot.SelectedObject)) yield return snapshot.SelectedObject;
        foreach (var node in snapshot.Nodes)
        {
            if (node.Id > 0 && seen.Add(node.Id)) yield return node.Id;
            if (node.VisualParent > 0 && seen.Add(node.VisualParent)) yield return node.VisualParent;
            if (node.LogicalParent > 0 && seen.Add(node.LogicalParent)) yield return node.LogicalParent;
            if (node.TemplatedParent > 0 && seen.Add(node.TemplatedParent)) yield return node.TemplatedParent;
        }
        foreach (var property in snapshot.Properties)
            if (property.ObjectId > 0 && seen.Add(property.ObjectId)) yield return property.ObjectId;
        foreach (var relation in snapshot.Relations)
        {
            if (relation.Source > 0 && seen.Add(relation.Source)) yield return relation.Source;
            if (relation.Target > 0 && seen.Add(relation.Target)) yield return relation.Target;
        }
    }
}
