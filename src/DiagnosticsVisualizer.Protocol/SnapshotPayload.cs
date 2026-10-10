using System.Security.Cryptography;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

/// <summary>Shared bounded snapshot representation used by full checkpoints and stateful deltas.</summary>
internal static class SnapshotPayload
{
    public static SnapshotData Copy(SnapshotData value) => value with
    { Nodes = value.Nodes.ToArray(), Properties = value.Properties.ToArray(), Relations = value.Relations.ToArray() };

    public static byte[] Hash(SnapshotData value)
    {
        var w = new WireWriter(); Write(w, value); return SHA256.HashData(w.Written);
    }
    public static void Validate(SnapshotData value)
    {
        if (value.Nodes.Length > TraceLimits.MaxNodesPerSnapshot || value.Properties.Length > TraceLimits.MaxPropertiesPerSnapshot ||
            value.Relations.Length > TraceLimits.MaxRelationsPerSnapshot) throw new InvalidDataException("Snapshot exceeds its declared budget.");
        var identities = new HashSet<long>();
        foreach (var n in value.Nodes)
            if (n is null || !identities.Add(n.Id) || n.Id <= 0 || n.VisualParent < 0 || n.LogicalParent < 0 || n.TemplatedParent < 0 ||
                !double.IsFinite(n.X) || !double.IsFinite(n.Y) || !double.IsFinite(n.Width) || !double.IsFinite(n.Height) || n.Width < 0 || n.Height < 0)
                throw new InvalidDataException("Invalid or duplicate snapshot node.");
    }
    public static void Write(WireWriter w, SnapshotData value)
    {
        Validate(value); w.I64(value.SelectedObject); w.Byte(value.Truncated ? (byte)1 : (byte)0);
        w.Count(value.Nodes.Length, TraceLimits.MaxNodesPerSnapshot);
        foreach (var n in value.Nodes)
        {
            w.I64(n.Id); w.I64(n.VisualParent); w.I64(n.LogicalParent); w.I64(n.TemplatedParent);
            w.Text(n.Type); w.Text(n.Name); w.Double(n.X); w.Double(n.Y); w.Double(n.Width); w.Double(n.Height);
            w.Byte(n.Visible ? (byte)1 : (byte)0); w.Text(n.Classes);
        }
        WriteProperties(w, value.Properties); WriteRelations(w, value.Relations);
    }
    public static SnapshotData Read(ref WireReader r)
    {
        var selected = r.I64(); var truncated = r.Bool();
        var nodes = new NodeSnapshot[r.Count(TraceLimits.MaxNodesPerSnapshot)];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = new(r.I64(), r.I64(), r.I64(), r.I64(), r.Text(), r.Text(), r.Double(), r.Double(), r.Double(), r.Double(), r.Bool(), r.Text());
        var value = new SnapshotData(nodes, ReadProperties(ref r), ReadRelations(ref r), selected, truncated);
        Validate(value); return value;
    }
    public static void WriteProperties(WireWriter w, PropertySnapshot[] properties)
    {
        w.Count(properties.Length, TraceLimits.MaxPropertiesPerSnapshot);
        foreach (var p in properties)
        { w.I64(p.ObjectId); w.Text(p.Name); w.Text(p.Kind); w.Text(p.Value); w.Text(p.Priority); w.Text(p.Source); }
    }
    public static PropertySnapshot[] ReadProperties(ref WireReader r)
    {
        var properties = new PropertySnapshot[r.Count(TraceLimits.MaxPropertiesPerSnapshot)];
        for (var i = 0; i < properties.Length; i++) properties[i] = new(r.I64(), r.Text(), r.Text(), r.Text(), r.Text(), r.Text());
        return properties;
    }
    public static void WriteRelations(WireWriter w, RelationSnapshot[] relations)
    {
        w.Count(relations.Length, TraceLimits.MaxRelationsPerSnapshot);
        foreach (var relation in relations) { w.I64(relation.Source); w.I64(relation.Target); w.Text(relation.Kind); w.Text(relation.Detail); }
    }
    public static RelationSnapshot[] ReadRelations(ref WireReader r)
    {
        var relations = new RelationSnapshot[r.Count(TraceLimits.MaxRelationsPerSnapshot)];
        for (var i = 0; i < relations.Length; i++) relations[i] = new(r.I64(), r.I64(), r.Text(), r.Text());
        return relations;
    }
}
