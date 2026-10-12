using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

// This measures viewer ingestion/publication, not instrumentation overhead or rendering.
// No timing threshold: exact retained contents are tested separately, and this benchmark
// rejects mismatched checksums/counters rather than making CI hardware-speed assumptions.
if (args.Length > 1) throw new ArgumentException("Usage: DiagnosticsVisualizer.Benchmarks [report.json]");
const int initialCount = 100_000, publications = 12, batchSize = 256, trials = 3;
var session = Guid.NewGuid(); var random = new Random(20261012);
TraceEvent Event(long id, long timestamp) => new(id, 0, timestamp, 1, 0, 0, 1, 1, Subsystem.Application);
var initial = Enumerable.Range(1, initialCount).Select(i => Event(i, random.NextInt64(0, initialCount * 4))).ToArray();
var batches = Enumerable.Range(0, publications).Select(batch => Enumerable.Range(1, batchSize).Select(i =>
{
    var id = initialCount + batch * batchSize + i;
    return Event(id, id * 4 - random.Next(0, 4096));
}).ToArray()).ToArray();
var results = new List<Measurement>();
_ = Run(false, false, 2048); _ = Run(true, false, 2048);
foreach (var bounded in new[] { false, true })
{
    for (var trial = 0; trial < trials; trial++)
    {
        Measurement baseline, indexed;
        if (trial % 2 == 0) { baseline = Run(false, bounded); indexed = Run(true, bounded); }
        else { indexed = Run(true, bounded); baseline = Run(false, bounded); }
        if (baseline.Checksum != indexed.Checksum || baseline.Evicted != indexed.Evicted || baseline.Retained != indexed.Retained)
            throw new InvalidOperationException("Benchmark strategies produced different capture contents.");
        results.Add(baseline); results.Add(indexed);
    }
}
var summary = results.GroupBy(x => new { x.Mode, x.Strategy }).Select(group => new
{
    group.Key.Mode, group.Key.Strategy,
    MedianTotalMilliseconds = group.Select(x => x.Milliseconds).Order().ElementAt(trials / 2),
    MedianAllocatedBytes = group.Select(x => x.AllocatedBytes).Order().ElementAt(trials / 2),
    group.First().Retained, group.First().Evicted, group.First().Checksum
}).ToArray();
var report = JsonSerializer.Serialize(new
{
    Scope = "Viewer ingestion + chronological publication; not target probe overhead, CPU utilization, or UI rendering",
    Baseline = "Arrival-bounded queue + full OrderBy(Timestamp).ThenBy(Id) on every publication",
    Candidate = "TraceStore incremental chronological merge with independently owned published arrays",
    Runtime = RuntimeInformation.FrameworkDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    InitialEvents = initialCount, Publications = publications, BatchSize = batchSize, Trials = trials,
    ChecksumsMatch = true, Summary = summary, Samples = results
}, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 1)
{
    var path = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, report);
}
Console.WriteLine(report);

Measurement Run(bool indexed, bool bounded, int initialLength = initialCount)
{
    var capacity = bounded ? initialLength : initialLength + publications * batchSize;
    var store = indexed ? new TraceStore(capacity) : null;
    var queue = indexed ? null : new Queue<TraceEvent>(initialLength + batchSize);
    long evicted = 0;
    void Add(int sequence, TraceEvent[] events)
    {
        if (store is not null) store.Add(new() { SessionId = session, Sequence = sequence, Frequency = 1000, Events = events });
        else
        {
            foreach (var e in events) queue!.Enqueue(e);
            while (queue!.Count > capacity) { queue.Dequeue(); evicted++; }
        }
    }
    TraceEvent[] Publish() => store?.GetView().Events ?? queue!.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToArray();
    Add(1, initial[..initialLength]); _ = Publish();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long ticks = 0, allocations = 0; ulong checksum = 14695981039346656037;
    var retained = 0;
    for (var batch = 0; batch < batches.Length; batch++)
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        Add(batch + 2, batches[batch]); var events = Publish();
        ticks += Stopwatch.GetTimestamp() - start; allocations += GC.GetAllocatedBytesForCurrentThread() - bytes;
        // Checksums run outside the measured interval and force consumption of every event.
        foreach (var e in events) unchecked { checksum = (checksum ^ (ulong)e.Id) * 1099511628211; checksum = (checksum ^ (ulong)e.Timestamp) * 1099511628211; }
        retained = events.Length;
    }
    if (store is not null) evicted = store.GetView().EvictedEvents;
    return new(bounded ? "Steady-state retention" : "Growing recording", indexed ? "Incremental merge" : "Full re-sort",
        ticks * 1000d / Stopwatch.Frequency, allocations, retained, evicted, checksum.ToString("X16"));
}

internal sealed record Measurement(string Mode, string Strategy, double Milliseconds, long AllocatedBytes, int Retained, long Evicted, string Checksum);
