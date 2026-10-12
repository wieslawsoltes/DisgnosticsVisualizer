using System.Text.Json;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Analysis;

public static class TraceExport
{
    public static async Task WriteBinaryAsync(Stream stream, TraceView trace, CancellationToken cancellationToken = default)
    {
        long sequence = 0;
        var session = trace.SessionId == Guid.Empty ? Guid.NewGuid() : trace.SessionId;
        var exportedObjects = new HashSet<long>();
        ObjectInfo[] Objects(IEnumerable<TraceEvent> events, SnapshotData? snapshot)
        {
            var result = TraceObjectReferences.Enumerate(events, snapshot).Where(trace.Objects.ContainsKey)
                .Take(TraceLimits.MaxObjectsPerBlock).Select(id => trace.Objects[id]).ToArray();
            foreach (var item in result) exportedObjects.Add(item.Id);
            return result;
        }
        async Task Write(TraceEvent[] events, Symbol[] symbols, ObjectInfo[] objects, SnapshotData? snapshot)
        {
            await TraceFile.WriteAsync(stream, new TraceBlock
            {
                SessionId = session, Sequence = ++sequence, Frequency = trace.Frequency,
                OriginTimestamp = trace.OriginTimestamp, DroppedEvents = trace.DroppedEvents,
                ProbeFaults = trace.ProbeFaults, MetadataDrops = trace.MetadataDrops, Recording = false,
                Symbols = symbols, Objects = objects, Events = events, Snapshot = snapshot
            }, cancellationToken).ConfigureAwait(false);
        }
        async Task Events(TraceEvent[] events, SnapshotData? snapshot)
        {
            var names = events.Select(e => e.NameId).Distinct().Where(trace.Symbols.ContainsKey).Select(id => new Symbol(id,
                trace.SourceSites.TryGetValue(id, out var site) ? SourceSiteSymbol.Encode(site) : trace.Symbols[id])).ToArray();
            await Write(events, names, Objects(events, snapshot), snapshot).ConfigureAwait(false);
        }
        // Rebatching removes original sequence gaps and viewer eviction history. Preserve those
        // facts in an unused metadata symbol instead of inventing target events or inflating drops.
        if (trace.MissingBlocks != 0 || trace.EvictedEvents != 0)
        {
            var usedNames = trace.Symbols.Keys.Concat(trace.SourceSites.Keys).Concat(trace.Events.Select(e => e.NameId)).ToHashSet();
            var id = int.MaxValue;
            while (usedNames.Contains(id)) id--;
            if (id <= 0) throw new InvalidDataException("No unused symbol identity for capture provenance.");
            await Write([], [new(id, TraceIntegritySymbol.Encode(new(trace.MissingBlocks, trace.EvictedEvents)))], [], null).ConfigureAwait(false);
        }
        // Retained snapshots must survive an export even when the latest checkpoint correctly
        // observes an empty tree at shutdown. Rebatch by end watermark; preserve every event's
        // original identity/timestamps. This is observation order, not a transactional replay.
        var ordered = trace.Events.OrderBy(e => e.End).ThenBy(e => e.Id).ToArray(); var cursor = 0;
        var checkpoints = trace.Checkpoints.ToList();
        if (checkpoints.Count == 0 || !ReferenceEquals(checkpoints[^1].Data, trace.Snapshot))
            checkpoints.Add(new(0, ordered.Length == 0 ? 0 : ordered[^1].End, trace.Snapshot));
        foreach (var checkpoint in checkpoints)
        {
            while (cursor < ordered.Length && ordered[cursor].End <= checkpoint.EventWatermark)
            {
                var start = cursor;
                while (cursor < ordered.Length && cursor - start < 4096 && ordered[cursor].End <= checkpoint.EventWatermark) cursor++;
                await Events(ordered[start..cursor], null).ConfigureAwait(false);
            }
            await Events([], checkpoint.Data).ConfigureAwait(false);
        }
        while (cursor < ordered.Length)
        {
            var end = Math.Min(ordered.Length, cursor + 4096);
            await Events(ordered[cursor..end], null).ConfigureAwait(false); cursor = end;
        }
        // A checkpoint can refer to more metadata than one frame permits. Preserve its closure
        // through bounded metadata-only batches; never silently discard snapshot-only nodes.
        foreach (var objects in checkpoints.SelectMany(c => TraceObjectReferences.Enumerate([], c.Data)).Distinct()
            .Where(id => !exportedObjects.Contains(id) && trace.Objects.ContainsKey(id))
            .Select(id => trace.Objects[id]).Chunk(TraceLimits.MaxObjectsPerBlock))
            await Write([], [], objects, null).ConfigureAwait(false);
    }
    /// <summary>Chrome Trace Event JSON, importable in Perfetto. CPU spans are never labelled GPU timings.</summary>
    public static async Task WriteChromeJsonAsync(Stream stream, TraceView trace, CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject(); writer.WriteString("displayTimeUnit", "ms");
        writer.WriteStartArray("traceEvents");
        foreach (var thread in trace.Events.Select(e => e.ThreadId).Distinct())
        {
            writer.WriteStartObject(); writer.WriteString("name", "thread_name"); writer.WriteString("ph", "M");
            writer.WriteNumber("pid", 1); writer.WriteNumber("tid", thread); writer.WriteStartObject("args");
            writer.WriteString("name", $"Managed thread {thread}"); writer.WriteEndObject(); writer.WriteEndObject();
        }
        var resourceLookups = new ResourceLookupIndex(trace);
        var count = 0;
        foreach (var e in trace.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject(); writer.WriteString("name", trace.Name(e)); writer.WriteString("cat", e.Subsystem.ToString());
            var asyncStart = AsyncOperationEvent.IsBegin(e); var asyncEnd = AsyncOperationEvent.IsEnd(e);
            writer.WriteString("ph", asyncStart ? "b" : asyncEnd ? "e" : e.Kind == TraceEventKind.Span ? "X" : "i");
            if (asyncStart || asyncEnd)
            { writer.WriteString("id", $"async-{(asyncStart ? e.Id : e.ParentId)}"); writer.WriteString("scope", "avtrace-async"); }
            writer.WriteNumber("pid", 1); writer.WriteNumber("tid", e.ThreadId);
            writer.WriteNumber("ts", (e.Timestamp - (double)trace.OriginTimestamp) * 1_000_000 / trace.Frequency);
            if (e.Kind == TraceEventKind.Span) writer.WriteNumber("dur", e.Duration * 1_000_000d / trace.Frequency); else if (!asyncStart && !asyncEnd) writer.WriteString("s", "t");
            writer.WriteStartObject("args"); writer.WriteString("eventId", e.Id.ToString());
            writer.WriteString("parentId", e.ParentId.ToString()); writer.WriteString("objectId", e.ObjectId.ToString());
            writer.WriteString("relatedObjectId", e.RelatedId.ToString());
            if (asyncStart || asyncEnd) writer.WriteString("measurement", "Async state-machine lifetime, including suspension; not CPU time");
            if (asyncEnd) writer.WriteString("outcome", ((AsyncOperationOutcome)e.Data1).ToString());
            if (RoutedDispatchEvent.IsValid(e))
            {
                writer.WriteString("routedDispatchId", e.Data1.ToString()); writer.WriteString("routedStep", RoutedDispatchEvent.Kind(e).ToString());
                writer.WriteString("route", RoutedDispatchEvent.RouteName(RoutedDispatchEvent.Route(e)));
                writer.WriteBoolean("handledBefore", RoutedDispatchEvent.HandledBefore(e)); writer.WriteBoolean("handledAfter", RoutedDispatchEvent.HandledAfter(e));
                writer.WriteBoolean("handledEventsToo", RoutedDispatchEvent.HandledEventsToo(e)); writer.WriteBoolean("returned", RoutedDispatchEvent.Returned(e));
            }
            if (ResourceLookupEvent.IsValid(e) && resourceLookups.Find(ResourceLookupEvent.LookupId(e)) is { } lookup)
            {
                writer.WriteString("resourceLookupId", lookup.Id.ToString()); writer.WriteString("resourceParentId", lookup.ParentLookupId.ToString());
                writer.WriteString("resourceKey", lookup.Key); writer.WriteString("resourceTheme", lookup.Theme);
                writer.WriteString("resourceOutcome", lookup.Status); writer.WriteString("resourceProvider", lookup.Provider);
                writer.WriteString("resourceResultIdentity", lookup.ValueId.ToString()); writer.WriteBoolean("resourceCaptureIncomplete", lookup.CaptureIncomplete);
            }
            if (trace.SourceSites.TryGetValue(e.NameId, out var site))
            {
                writer.WriteString("source", site.Location); writer.WriteString("methodIdentity", site.Identity);
                writer.WriteString("sourceChecksum", site.Checksum); writer.WriteString("sourceUrl", site.SourceUrl);
                writer.WriteBoolean("asyncKickoff", site.AsyncKickoff);
            }
            writer.WriteEndObject(); writer.WriteEndObject();
            if (++count % 4096 == 0) await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        // Cross-thread or out-of-span parentage is a causal handoff, not CPU nesting.
        var byId = trace.Events.ToDictionary(e => e.Id);
        foreach (var child in trace.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(child.ParentId, out var parent) || parent.Timestamp > child.Timestamp ||
                (parent.ThreadId == child.ThreadId && child.End <= parent.End)) continue;
            WriteFlow(writer, trace, child.Id, parent.ThreadId, Math.Min(parent.End, child.Timestamp), "s");
            WriteFlow(writer, trace, child.Id, child.ThreadId, child.Timestamp, "f");
            if (++count % 4096 == 0) await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        writer.WriteEndArray(); writer.WriteStartObject("diagnosticsVisualizer");
        writer.WriteString("session", trace.SessionId); writer.WriteNumber("droppedEvents", trace.DroppedEvents);
        writer.WriteNumber("missingBlocks", trace.MissingBlocks); writer.WriteNumber("probeFaults", trace.ProbeFaults);
        writer.WriteNumber("metadataDrops", trace.MetadataDrops); writer.WriteNumber("evictedEvents", trace.EvictedEvents); writer.WriteEndObject(); writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    private static void WriteFlow(Utf8JsonWriter writer, TraceView trace, long id, int thread, long timestamp, string phase)
    {
        writer.WriteStartObject(); writer.WriteString("name", "Recorded causal handoff");
        writer.WriteString("cat", "causality"); writer.WriteString("ph", phase); writer.WriteString("id", $"causal-{id}");
        writer.WriteNumber("pid", 1); writer.WriteNumber("tid", thread);
        writer.WriteNumber("ts", (timestamp - (double)trace.OriginTimestamp) * 1_000_000 / trace.Frequency);
        if (phase == "f") writer.WriteString("bp", "e");
        writer.WriteEndObject();
    }
}

public sealed record CostComparison(string Name, Subsystem Subsystem, int BaselineCount, int CurrentCount,
    double BaselineSelfMs, double CurrentSelfMs, double DeltaMs, double? Percent);

public static class TraceComparison
{
    public static CostComparison[] Compare(AnalysisResult baseline, AnalysisResult current)
    {
        // Builds have different MVIDs; comparison intentionally aggregates by displayed signature.
        static Dictionary<(string Name, Subsystem Subsystem), (int Count, double SelfMs)> Aggregate(AnalysisResult result)
            => result.Costs.GroupBy(row => (row.Name, row.Subsystem)).ToDictionary(g => g.Key, g => (g.Sum(r => r.Count), g.Sum(r => r.SelfMs)));
        var before = Aggregate(baseline); var after = Aggregate(current);
        return before.Keys.Union(after.Keys).Select(key =>
        {
            before.TryGetValue(key, out var a); after.TryGetValue(key, out var b);
            var av = a.SelfMs; var bv = b.SelfMs;
            return new CostComparison(key.Name, key.Subsystem, a.Count, b.Count,
                av, bv, bv - av, av == 0 ? null : (bv - av) / av * 100);
        }).OrderByDescending(row => row.DeltaMs).ToArray();
    }
}
