using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record GpuTimingRow(long Id, long ContextId, string Name, string Status, GpuTimingSite? Site,
    TraceEvent? Begin, TraceEvent? Submitted, TraceEvent? Completed, ulong? DeviceStartNs, ulong? DeviceEndNs,
    double? SubmissionMs, double? ReadbackMs)
{
    public bool IsAvailable => Status == nameof(GpuQueryOutcome.Available);
    public ulong? ElapsedNs => IsAvailable && DeviceStartNs is { } a && DeviceEndNs is { } b ? b - a : null;
    public double? GpuMs => ElapsedNs / 1_000_000d;
    public long EventId => Begin?.Id ?? Completed?.Id ?? Id;
    public string Renderer => Site?.Renderer ?? "Unknown";
}

/// <summary>Device differences are never positioned on a CPU timeline without clock calibration.</summary>
public sealed class GpuTimingIndex
{
    public GpuTimingRow[] Rows { get; }
    public GpuTimingIndex(TraceView trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Rows = trace.Events.Where(e => GpuTimingEvent.IsMarker(e))
            .GroupBy(e => e.Data0 == GpuTimingEvent.Begin ? e.Id : e.ParentId).Select(Parse)
            .OrderByDescending(r => r.GpuMs ?? -1).ThenBy(r => r.Id).ToArray();
        GpuTimingRow Parse(IGrouping<long, TraceEvent> group)
        {
            var entries = group.ToArray(); var first = entries[0];
            var invalid = entries.Any(e => !GpuTimingEvent.IsValid(e) || e.ObjectId != first.ObjectId ||
                e.NameId != first.NameId || e.ThreadId != first.ThreadId) || entries.GroupBy(e => e.Data0).Any(g => g.Count() != 1);
            TraceEvent? Find(long code) => entries.FirstOrDefault(e => e.Data0 == code) is { Id: > 0 } value ? value : null;
            var begin = Find(GpuTimingEvent.Begin); var submitted = Find(GpuTimingEvent.Submitted); var complete = Find(GpuTimingEvent.Completed);
            var start = Find(GpuTimingEvent.CounterStart); var end = Find(GpuTimingEvent.CounterEnd);
            trace.GpuSites.TryGetValue(first.NameId, out var site);
            invalid |= begin is { } early && submitted is { } late && late.Timestamp < early.Timestamp;
            var status = "Incomplete";
            if (begin is { } b && complete is { } c && site is not null)
            {
                invalid |= c.Timestamp < b.Timestamp || entries.Any(e => e.Timestamp < b.Timestamp || e.Timestamp > c.Timestamp);
                if (submitted is { } s) invalid |= s.Timestamp < b.Timestamp || s.Timestamp > c.Timestamp ||
                    start is { } ds && ds.Timestamp < s.Timestamp || end is { } de && de.Timestamp < s.Timestamp;
                var outcome = (GpuQueryOutcome)c.Data1;
                if (outcome == GpuQueryOutcome.Available)
                {
                    if (submitted is not null && start is { } a && end is { } z)
                    {
                        invalid |= z.Timestamp < a.Timestamp || site.CounterBits != 64 || unchecked((ulong)z.Data1) < unchecked((ulong)a.Data1);
                        status = nameof(GpuQueryOutcome.Available);
                    }
                }
                else
                {
                    invalid |= start is not null || end is not null;
                    status = Enum.IsDefined(outcome) ? outcome.ToString() : "Invalid markers";
                }
            }
            if (invalid) status = "Invalid markers";
            var valid = status != "Invalid markers";
            return new(group.Key, first.ObjectId, site?.Name ?? trace.Name(first), status, site, begin, submitted, complete,
                valid && start is { } sc ? unchecked((ulong)sc.Data1) : null, valid && end is { } ec ? unchecked((ulong)ec.Data1) : null,
                valid && begin is { } cb && submitted is { } cs ? trace.Milliseconds(cs.Timestamp - cb.Timestamp) : null,
                valid && submitted is { } ct && complete is { } cc ? trace.Milliseconds(cc.Timestamp - ct.Timestamp) : null);
        }
    }
}
