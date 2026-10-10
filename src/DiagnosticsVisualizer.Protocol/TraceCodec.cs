using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

/// <summary>Batch-local zig-zag/varint deltas; no dependency on previous packets.</summary>
public static class TraceCodec
{
    public static byte[] Encode(TraceBlock block)
    {
        if (block.Frequency <= 0) throw new InvalidDataException("Clock frequency must be positive.");
        var w = new WireWriter();
        w.Bytes(block.SessionId.ToByteArray()); w.I64(block.Sequence); w.I64(block.Frequency);
        w.I64(block.OriginTimestamp); w.I64(block.UtcStartTicks);
        w.I64(block.DroppedEvents); w.I64(block.ProbeFaults); w.I64(block.MetadataDrops);
        w.Byte(block.Recording ? (byte)1 : (byte)0);
        w.Count(block.Symbols.Length, TraceLimits.MaxSymbolsPerBlock);
        foreach (var symbol in block.Symbols) { w.I64(symbol.Id); w.Text(symbol.Name); }
        w.Count(block.Objects.Length, TraceLimits.MaxObjectsPerBlock);
        foreach (var obj in block.Objects) { w.I64(obj.Id); w.Text(obj.Type); w.Text(obj.Display); }
        w.Count(block.Events.Length, TraceLimits.MaxEventsPerBlock);
        var previous = default(TraceEvent);
        foreach (var e in block.Events)
        {
            if (e.Duration < 0 || !Enum.IsDefined(e.Subsystem) || !Enum.IsDefined(e.Kind)) throw new InvalidDataException("Invalid event.");
            w.I64(unchecked(e.Id - previous.Id)); w.I64(e.ParentId);
            w.I64(unchecked(e.Timestamp - previous.Timestamp)); w.U64((ulong)e.Duration);
            w.I64(unchecked(e.ObjectId - previous.ObjectId)); w.I64(unchecked(e.RelatedId - previous.RelatedId));
            w.I64((long)e.ThreadId - previous.ThreadId); w.I64((long)e.NameId - previous.NameId);
            w.Byte((byte)e.Subsystem); w.Byte((byte)e.Kind); w.I64(e.Data0); w.I64(e.Data1);
            previous = e;
        }
        w.Byte(block.Snapshot is null ? (byte)0 : (byte)1);
        if (block.Snapshot is { } snapshot)
        {
            w.I64(snapshot.SelectedObject); w.Byte(snapshot.Truncated ? (byte)1 : (byte)0);
            w.Count(snapshot.Nodes.Length, TraceLimits.MaxNodesPerSnapshot);
            foreach (var n in snapshot.Nodes)
            {
                w.I64(n.Id); w.I64(n.VisualParent); w.I64(n.LogicalParent); w.I64(n.TemplatedParent);
                w.Text(n.Type); w.Text(n.Name); w.Double(n.X); w.Double(n.Y); w.Double(n.Width); w.Double(n.Height);
                w.Byte(n.Visible ? (byte)1 : (byte)0); w.Text(n.Classes);
            }
            w.Count(snapshot.Properties.Length, TraceLimits.MaxPropertiesPerSnapshot);
            foreach (var p in snapshot.Properties)
            { w.I64(p.ObjectId); w.Text(p.Name); w.Text(p.Kind); w.Text(p.Value); w.Text(p.Priority); w.Text(p.Source); }
            w.Count(snapshot.Relations.Length, TraceLimits.MaxRelationsPerSnapshot);
            foreach (var r in snapshot.Relations) { w.I64(r.Source); w.I64(r.Target); w.Text(r.Kind); w.Text(r.Detail); }
        }
        return WireEnvelope.Seal(FrameKind.Trace, w.Written);
    }

    public static TraceBlock Decode(ReadOnlySpan<byte> frame)
    {
        var r = new WireReader(WireEnvelope.Open(frame, FrameKind.Trace));
        var session = new Guid(r.Bytes(16)); var sequence = r.I64(); var frequency = r.I64();
        if (frequency <= 0 || sequence < 0) throw new InvalidDataException("Invalid session clock/sequence.");
        var origin = r.I64(); var utc = r.I64();
        var dropped = r.I64(); var faults = r.I64(); var metadataDrops = r.I64();
        if (dropped < 0 || faults < 0 || metadataDrops < 0) throw new InvalidDataException("Negative counters.");
        var recording = r.Bool();
        var symbols = new Symbol[r.Count(TraceLimits.MaxSymbolsPerBlock)];
        for (var i = 0; i < symbols.Length; i++) symbols[i] = new(r.Int(), r.Text());
        var objects = new ObjectInfo[r.Count(TraceLimits.MaxObjectsPerBlock)];
        for (var i = 0; i < objects.Length; i++) objects[i] = new(r.I64(), r.Text(), r.Text());
        var events = new TraceEvent[r.Count(TraceLimits.MaxEventsPerBlock)];
        var previous = default(TraceEvent);
        for (var i = 0; i < events.Length; i++)
        {
            var id = unchecked(previous.Id + r.I64()); var parent = r.I64();
            var timestamp = unchecked(previous.Timestamp + r.I64()); var duration = r.NonNegative();
            var obj = unchecked(previous.ObjectId + r.I64()); var related = unchecked(previous.RelatedId + r.I64());
            var thread = (long)previous.ThreadId + r.I64(); var name = (long)previous.NameId + r.I64();
            if (thread is < int.MinValue or > int.MaxValue || name is < int.MinValue or > int.MaxValue)
                throw new InvalidDataException("Invalid event identifiers.");
            var subsystem = (Subsystem)r.Byte(); var kind = (TraceEventKind)r.Byte();
            if (!Enum.IsDefined(subsystem) || !Enum.IsDefined(kind)) throw new InvalidDataException("Unknown event kind.");
            var data0 = r.I64(); var data1 = r.I64();
            events[i] = previous = new(id, parent, timestamp, duration, obj, related,
                (int)thread, (int)name, subsystem, kind, data0, data1);
        }
        SnapshotData? snapshot = null;
        if (r.Bool())
        {
            var selected = r.I64(); var truncated = r.Bool();
            var nodes = new NodeSnapshot[r.Count(TraceLimits.MaxNodesPerSnapshot)];
            for (var i = 0; i < nodes.Length; i++)
                nodes[i] = new(r.I64(), r.I64(), r.I64(), r.I64(), r.Text(), r.Text(),
                    r.Double(), r.Double(), r.Double(), r.Double(), r.Bool(), r.Text());
            var properties = new PropertySnapshot[r.Count(TraceLimits.MaxPropertiesPerSnapshot)];
            for (var i = 0; i < properties.Length; i++) properties[i] = new(r.I64(), r.Text(), r.Text(), r.Text(), r.Text(), r.Text());
            var relations = new RelationSnapshot[r.Count(TraceLimits.MaxRelationsPerSnapshot)];
            for (var i = 0; i < relations.Length; i++) relations[i] = new(r.I64(), r.I64(), r.Text(), r.Text());
            snapshot = new(nodes, properties, relations, selected, truncated);
        }
        r.Finish();
        return new TraceBlock { SessionId = session, Sequence = sequence, Frequency = frequency,
            OriginTimestamp = origin, UtcStartTicks = utc, DroppedEvents = dropped, ProbeFaults = faults,
            MetadataDrops = metadataDrops, Recording = recording, Symbols = symbols, Objects = objects,
            Events = events, Snapshot = snapshot };
    }
}
