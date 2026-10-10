using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

/// <summary>Independent blocks, interned metadata, zig-zag timestamps/identities. No inherited codec state across UDP loss.</summary>
public static class TraceCodec
{
    public static byte[] Encode(TraceBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(block.Frequency));
        var w = new WireWriter();
        w.Bytes(block.SessionId.ToByteArray()); w.NonNegative(block.Sequence);
        w.NonNegative(block.Frequency); w.I64(block.OriginTimestamp); w.I64(block.UtcStartTicks);
        w.NonNegative(block.DroppedEvents); w.NonNegative(block.ProbeFaults); w.NonNegative(block.MetadataDrops);
        w.Byte(block.Recording ? (byte)1 : (byte)0);
        w.Count(block.Symbols.Length, TraceLimits.MaxSymbolsPerBlock);
        foreach (var symbol in block.Symbols) { w.NonNegative(symbol.Id); w.Text(symbol.Name); }
        w.Count(block.Objects.Length, TraceLimits.MaxObjectsPerBlock);
        foreach (var obj in block.Objects) { w.NonNegative(obj.Id); w.Text(obj.Type); w.Text(obj.Display); }
        w.Count(block.Events.Length, TraceLimits.MaxEventsPerBlock);
        long previousId = 0, previousTimestamp = block.OriginTimestamp;
        foreach (var e in block.Events)
        {
            if (e.Id <= 0 || e.Duration < 0 || e.Timestamp > long.MaxValue - e.Duration || (int)e.Subsystem > 63 || !Enum.IsDefined(e.Kind)) throw new InvalidDataException("Invalid event.");
            w.I64(unchecked(e.Id - previousId)); w.I64(unchecked(e.Timestamp - previousTimestamp));
            w.I64(unchecked(e.ParentId - e.Id)); w.NonNegative(e.Duration); w.I64(e.ObjectId); w.I64(e.RelatedId);
            w.I64(e.ThreadId); w.NonNegative(e.NameId); w.Byte((byte)e.Subsystem); w.Byte((byte)e.Kind);
            w.I64(e.Data0); w.I64(e.Data1);
            previousId = e.Id; previousTimestamp = e.Timestamp;
        }
        w.Byte(block.Snapshot is null ? (byte)0 : (byte)1);
        if (block.Snapshot is { } snapshot) SnapshotPayload.Write(w, snapshot);
        return WireEnvelope.Seal(FrameKind.Trace, w.Written);
    }

    public static TraceBlock Decode(ReadOnlySpan<byte> frame)
    {
        var r = new WireReader(WireEnvelope.Open(frame, FrameKind.Trace));
        var session = new Guid(r.Bytes(16)); var sequence = r.NonNegative(); var frequency = r.NonNegative();
        if (frequency == 0 || sequence == 0) throw new InvalidDataException("Invalid clock or sequence.");
        var origin = r.I64(); var utc = r.I64(); var dropped = r.NonNegative(); var faults = r.NonNegative(); var metadataDrops = r.NonNegative(); var recording = r.Bool();
        var symbols = new Symbol[r.Count(TraceLimits.MaxSymbolsPerBlock)];
        for (var i = 0; i < symbols.Length; i++) symbols[i] = new(checked((int)r.NonNegative()), r.Text());
        var objects = new ObjectInfo[r.Count(TraceLimits.MaxObjectsPerBlock)];
        for (var i = 0; i < objects.Length; i++) objects[i] = new(r.NonNegative(), r.Text(), r.Text());
        var events = new TraceEvent[r.Count(TraceLimits.MaxEventsPerBlock)];
        long previousId = 0, timestamp = origin;
        for (var i = 0; i < events.Length; i++)
        {
            var id = unchecked(previousId + r.I64()); timestamp = unchecked(timestamp + r.I64());
            var parent = unchecked(id + r.I64()); var duration = r.NonNegative(); var objectId = r.I64(); var related = r.I64();
            var thread = checked((int)r.I64()); var name = checked((int)r.NonNegative()); var subsystem = (Subsystem)r.Byte(); var kind = (TraceEventKind)r.Byte();
            if (id <= 0 || timestamp > long.MaxValue - duration || (int)subsystem > 63 || !Enum.IsDefined(kind)) throw new InvalidDataException("Invalid event identity, category, duration, or kind.");
            events[i] = new(id, parent, timestamp, duration, objectId, related, thread, name, subsystem, kind, r.I64(), r.I64()); previousId = id;
        }
        SnapshotData? snapshot = null;
        if (r.Bool()) snapshot = SnapshotPayload.Read(ref r);
        r.Finish();
        return new()
        {
            SessionId = session, Sequence = sequence, Frequency = frequency, OriginTimestamp = origin, UtcStartTicks = utc,
            DroppedEvents = dropped, ProbeFaults = faults, MetadataDrops = metadataDrops, Recording = recording,
            Symbols = symbols, Objects = objects, Events = events, Snapshot = snapshot
        };
    }
}
