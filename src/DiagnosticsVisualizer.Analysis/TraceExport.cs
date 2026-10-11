using System.Text.Json;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Analysis;

public static class TraceExport
{
    public static async Task WriteBinaryAsync(Stream stream, TraceView trace, CancellationToken cancellationToken = default)
    {
        long sequence = 0;
        foreach (var events in trace.Events.Chunk(4096))
        {
            var names = events.Select(e => e.NameId).Distinct().Where(trace.Symbols.ContainsKey).Select(id => new Symbol(id, trace.SourceSites.TryGetValue(id, out var site) ? SourceSiteSymbol.Encode(site) : trace.Symbols[id])).ToArray();
            var objects = events.SelectMany(e => new[] { e.ObjectId, e.RelatedId }).Distinct().Where(trace.Objects.ContainsKey).Select(id => trace.Objects[id]).ToArray();
            await TraceFile.WriteAsync(stream, new TraceBlock
            {
                SessionId = trace.SessionId, Sequence = ++sequence, Frequency = trace.Frequency,
                OriginTimestamp = trace.OriginTimestamp, DroppedEvents = trace.DroppedEvents,
                ProbeFaults = trace.ProbeFaults, MetadataDrops = trace.MetadataDrops, Recording = false,
                Symbols = names, Objects = objects, Events = events, Snapshot = sequence == 1 ? trace.Snapshot : null
            }, cancellationToken).ConfigureAwait(false);
        }
        if (sequence == 0) await TraceFile.WriteAsync(stream, new()
        { SessionId = trace.SessionId == Guid.Empty ? Guid.NewGuid() : trace.SessionId, Sequence = 1, Frequency = trace.Frequency,
          OriginTimestamp = trace.OriginTimestamp, Snapshot = trace.Snapshot, Recording = false }, cancellationToken).ConfigureAwait(false);
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
        var count = 0;
        foreach (var e in trace.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject(); writer.WriteString("name", trace.Name(e)); writer.WriteString("cat", e.Subsystem.ToString());
            writer.WriteString("ph", e.Kind == TraceEventKind.Span ? "X" : "i");
            writer.WriteNumber("pid", 1); writer.WriteNumber("tid", e.ThreadId);
            writer.WriteNumber("ts", (e.Timestamp - (double)trace.OriginTimestamp) * 1_000_000 / trace.Frequency);
            if (e.Kind == TraceEventKind.Span) writer.WriteNumber("dur", e.Duration * 1_000_000d / trace.Frequency); else writer.WriteString("s", "t");
            writer.WriteStartObject("args"); writer.WriteString("eventId", e.Id.ToString());
            writer.WriteString("parentId", e.ParentId.ToString()); writer.WriteString("objectId", e.ObjectId.ToString());
            writer.WriteString("relatedObjectId", e.RelatedId.ToString());
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
        writer.WriteNumber("evictedEvents", trace.EvictedEvents); writer.WriteEndObject(); writer.WriteEndObject();
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
