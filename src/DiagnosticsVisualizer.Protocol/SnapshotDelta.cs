using System.Security.Cryptography;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

/// <summary>
/// Ordered-stream snapshot compression. Only changed node fields are transmitted; removed identities
/// and order changes are explicit. Properties/relations are replaced only when changed. Every delta
/// binds the exact previous snapshot sequence, session, clock and SHA-256 image. Event batches remain
/// independently decodable and must never depend on this state. A new connection always starts full.
/// Instances have a single reader/writer owner and are not thread safe.
/// </summary>
public sealed class TraceStreamEncoder(int checkpointInterval = 16)
{
    private SnapshotBaseline? _baseline;
    private int _sinceFull;
    public long DeltaFrames { get; private set; }
    public long SavedBytes { get; private set; }
    public void Reset() { _baseline = null; _sinceFull = 0; }
    public byte[] Encode(TraceBlock block)
    {
        if (checkpointInterval is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
        var full = TraceCodec.Encode(block);
        if (block.Snapshot is not { } snapshot) return full;
        var next = SnapshotBaseline.Create(block, snapshot);
        byte[]? delta = null;
        if (_baseline is { } previous && previous.Session == block.SessionId && previous.Sequence < block.Sequence &&
            previous.Frequency == block.Frequency && previous.Origin == block.OriginTimestamp && _sinceFull < checkpointInterval - 1)
        {
            try { delta = SnapshotDelta.Encode(previous, next, block); }
            catch (InvalidDataException error) when (error.Message == "Frame too large.") { /* A larger patch falls back to its already validated full image. */ }
        }
        _baseline = next;
        if (delta is not null && delta.Length < full.Length * .9)
        { _sinceFull++; DeltaFrames++; SavedBytes += full.Length - delta.Length; return delta; }
        _sinceFull = 0; return full;
    }
}

public sealed class TraceStreamDecoder
{
    private SnapshotBaseline? _baseline;
    public long DeltaFrames { get; private set; }
    public void Reset() => _baseline = null;
    public TraceBlock Decode(ReadOnlySpan<byte> frame)
    {
        if (WireEnvelope.GetKind(frame) == FrameKind.Trace)
        {
            var block = TraceCodec.Decode(frame);
            if (block.Snapshot is { } snapshot) _baseline = SnapshotBaseline.Create(block, snapshot);
            return block;
        }
        if (_baseline is null) throw new InvalidDataException("Snapshot delta requires a full checkpoint. Reconnect or reset to a checkpoint.");
        var result = SnapshotDelta.Decode(_baseline, frame);
        // Validation, content-hash comparison and all allocations complete before state publication.
        _baseline = SnapshotBaseline.Create(result, result.Snapshot!); DeltaFrames++; return result;
    }
}

internal sealed record SnapshotBaseline(Guid Session, long Sequence, long Frequency, long Origin, SnapshotData Data, byte[] Hash)
{
    public static SnapshotBaseline Create(TraceBlock block, SnapshotData snapshot)
    {
        var copy = SnapshotPayload.Copy(snapshot);
        return new(block.SessionId, block.Sequence, block.Frequency, block.OriginTimestamp, copy, SnapshotPayload.Hash(copy));
    }
}

internal static class SnapshotDelta
{
    [Flags]
    private enum Fields : ushort
    {
        None = 0, New = 1, VisualParent = 2, LogicalParent = 4, TemplatedParent = 8, Type = 16, Name = 32,
        X = 64, Y = 128, Width = 256, Height = 512, Visible = 1024, Classes = 2048, All = 4095
    }
    public static byte[] Encode(SnapshotBaseline previous, SnapshotBaseline next, TraceBlock block)
    {
        var w = new WireWriter(); w.Byte(1); w.Bytes(previous.Session.ToByteArray()); w.I64(previous.Sequence);
        w.Bytes(previous.Hash); w.Bytes(next.Hash);
        var batch = TraceCodec.Encode(block with { Snapshot = null }); w.Count(batch.Length, TraceLimits.MaxFrameBytes); w.Bytes(batch);
        var before = previous.Data.Nodes.ToDictionary(n => n.Id); var after = next.Data.Nodes.ToDictionary(n => n.Id);
        var removed = before.Keys.Where(id => !after.ContainsKey(id)).ToArray(); w.Count(removed.Length, TraceLimits.MaxNodesPerSnapshot);
        foreach (var id in removed) w.I64(id);
        var changed = next.Data.Nodes.Where(n => !before.TryGetValue(n.Id, out var old) || Changes(old, n) != Fields.None).ToArray();
        w.Count(changed.Length, TraceLimits.MaxNodesPerSnapshot);
        foreach (var n in changed)
        {
            before.TryGetValue(n.Id, out var old); var fields = Changes(old, n); w.I64(n.Id); w.U64((ulong)fields);
            if ((fields & Fields.VisualParent) != 0) w.I64(n.VisualParent);
            if ((fields & Fields.LogicalParent) != 0) w.I64(n.LogicalParent);
            if ((fields & Fields.TemplatedParent) != 0) w.I64(n.TemplatedParent);
            if ((fields & Fields.Type) != 0) w.Text(n.Type);
            if ((fields & Fields.Name) != 0) w.Text(n.Name);
            if ((fields & Fields.X) != 0) w.Double(n.X);
            if ((fields & Fields.Y) != 0) w.Double(n.Y);
            if ((fields & Fields.Width) != 0) w.Double(n.Width);
            if ((fields & Fields.Height) != 0) w.Double(n.Height);
            if ((fields & Fields.Visible) != 0) w.Byte(n.Visible ? (byte)1 : (byte)0);
            if ((fields & Fields.Classes) != 0) w.Text(n.Classes);
        }
        var ordered = !previous.Data.Nodes.Select(n => n.Id).Where(after.ContainsKey).SequenceEqual(next.Data.Nodes.Select(n => n.Id));
        w.Byte(ordered ? (byte)1 : (byte)0);
        if (ordered) { w.Count(next.Data.Nodes.Length, TraceLimits.MaxNodesPerSnapshot); foreach (var n in next.Data.Nodes) w.I64(n.Id); }
        w.I64(next.Data.SelectedObject); w.Byte(next.Data.Truncated ? (byte)1 : (byte)0);
        var propertiesChanged = !previous.Data.Properties.SequenceEqual(next.Data.Properties);
        w.Byte(propertiesChanged ? (byte)1 : (byte)0); if (propertiesChanged) SnapshotPayload.WriteProperties(w, next.Data.Properties);
        var relationsChanged = !previous.Data.Relations.SequenceEqual(next.Data.Relations);
        w.Byte(relationsChanged ? (byte)1 : (byte)0); if (relationsChanged) SnapshotPayload.WriteRelations(w, next.Data.Relations);
        return WireEnvelope.Seal(FrameKind.SnapshotDelta, w.Written);
    }
    public static TraceBlock Decode(SnapshotBaseline previous, ReadOnlySpan<byte> frame)
    {
        var r = new WireReader(WireEnvelope.Open(frame, FrameKind.SnapshotDelta));
        if (r.Byte() != 1 || new Guid(r.Bytes(16)) != previous.Session || r.I64() != previous.Sequence ||
            !CryptographicOperations.FixedTimeEquals(r.Bytes(32), previous.Hash)) throw new InvalidDataException("Snapshot baseline mismatch. A full checkpoint is required.");
        var expected = r.Bytes(32).ToArray(); var block = TraceCodec.Decode(r.Bytes(r.Count(TraceLimits.MaxFrameBytes)));
        if (block.Snapshot is not null || block.SessionId != previous.Session || block.Sequence <= previous.Sequence ||
            block.Frequency != previous.Frequency || block.OriginTimestamp != previous.Origin)
            throw new InvalidDataException("Snapshot delta changed its session, sequence or clock.");
        var nodes = previous.Data.Nodes.ToDictionary(n => n.Id); var modified = new HashSet<long>();
        var removed = r.Count(TraceLimits.MaxNodesPerSnapshot);
        for (var i = 0; i < removed; i++) if (!nodes.Remove(r.I64())) throw new InvalidDataException("Unknown or duplicate removed node.");
        var changed = r.Count(TraceLimits.MaxNodesPerSnapshot);
        for (var i = 0; i < changed; i++)
        {
            var id = r.I64(); var bits = r.U64();
            if (id <= 0 || bits == 0 || bits > (ulong)Fields.All || !modified.Add(id)) throw new InvalidDataException("Invalid snapshot node patch.");
            var fields = (Fields)bits; nodes.TryGetValue(id, out var old);
            if ((old is null) != fields.HasFlag(Fields.New) || (old is null && fields != Fields.All)) throw new InvalidDataException("Invalid new-node fields.");
            old ??= new(id, 0, 0, 0, "", "", 0, 0, 0, 0, false, "");
            var n = old with
            {
                VisualParent = fields.HasFlag(Fields.VisualParent) ? r.I64() : old.VisualParent,
                LogicalParent = fields.HasFlag(Fields.LogicalParent) ? r.I64() : old.LogicalParent,
                TemplatedParent = fields.HasFlag(Fields.TemplatedParent) ? r.I64() : old.TemplatedParent,
                Type = fields.HasFlag(Fields.Type) ? r.Text() : old.Type, Name = fields.HasFlag(Fields.Name) ? r.Text() : old.Name,
                X = fields.HasFlag(Fields.X) ? r.Double() : old.X, Y = fields.HasFlag(Fields.Y) ? r.Double() : old.Y,
                Width = fields.HasFlag(Fields.Width) ? r.Double() : old.Width, Height = fields.HasFlag(Fields.Height) ? r.Double() : old.Height,
                Visible = fields.HasFlag(Fields.Visible) ? r.Bool() : old.Visible, Classes = fields.HasFlag(Fields.Classes) ? r.Text() : old.Classes
            };
            nodes[id] = n;
            if (nodes.Count > TraceLimits.MaxNodesPerSnapshot) throw new InvalidDataException("Snapshot node budget exceeded.");
        }
        NodeSnapshot[] ordered;
        if (r.Bool())
        {
            ordered = new NodeSnapshot[r.Count(TraceLimits.MaxNodesPerSnapshot)]; var seen = new HashSet<long>();
            for (var i = 0; i < ordered.Length; i++)
            {
                var id = r.I64();
                if (!seen.Add(id) || !nodes.TryGetValue(id, out var node)) throw new InvalidDataException("Invalid snapshot order.");
                ordered[i] = node;
            }
        }
        else ordered = previous.Data.Nodes.Where(n => nodes.ContainsKey(n.Id)).Select(n => nodes[n.Id]).ToArray();
        if (ordered.Length != nodes.Count) throw new InvalidDataException("Snapshot order omits nodes.");
        var selected = r.I64(); var truncated = r.Bool();
        var properties = r.Bool() ? SnapshotPayload.ReadProperties(ref r) : previous.Data.Properties.ToArray();
        var relations = r.Bool() ? SnapshotPayload.ReadRelations(ref r) : previous.Data.Relations.ToArray(); r.Finish();
        var result = new SnapshotData(ordered, properties, relations, selected, truncated);
        if (!CryptographicOperations.FixedTimeEquals(SnapshotPayload.Hash(result), expected)) throw new InvalidDataException("Reconstructed snapshot checksum mismatch.");
        return block with { Snapshot = result };
    }
    private static Fields Changes(NodeSnapshot? a, NodeSnapshot b)
    {
        if (a is null) return Fields.All;
        var f = Fields.None;
        if (a.VisualParent != b.VisualParent) f |= Fields.VisualParent;
        if (a.LogicalParent != b.LogicalParent) f |= Fields.LogicalParent;
        if (a.TemplatedParent != b.TemplatedParent) f |= Fields.TemplatedParent;
        if (a.Type != b.Type) f |= Fields.Type;
        if (a.Name != b.Name) f |= Fields.Name;
        if (BitConverter.DoubleToInt64Bits(a.X) != BitConverter.DoubleToInt64Bits(b.X)) f |= Fields.X; if (BitConverter.DoubleToInt64Bits(a.Y) != BitConverter.DoubleToInt64Bits(b.Y)) f |= Fields.Y;
        if (BitConverter.DoubleToInt64Bits(a.Width) != BitConverter.DoubleToInt64Bits(b.Width)) f |= Fields.Width; if (BitConverter.DoubleToInt64Bits(a.Height) != BitConverter.DoubleToInt64Bits(b.Height)) f |= Fields.Height;
        if (a.Visible != b.Visible) f |= Fields.Visible; if (a.Classes != b.Classes) f |= Fields.Classes;
        return f;
    }
}
