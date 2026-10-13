using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record NativeMethodRow(int SiteId, string Assembly, string Method, string MethodIdentity, string Status,
    string Error, long Compilations, int Completions, int Unwound, int UnmatchedEntries, int UnmatchedCompletions, double TotalMs, long EventId);

/// <summary>Linear pairing with compact event-array offsets, not a second copy of the recorded event payloads.</summary>
public sealed class NativeCaptureIndex
{
    public NativeProcessInfo? Process { get; }
    public NativeCaptureEnd? End { get; }
    public NativeControlState? Control { get; }
    public NativeMethodRow[] Methods { get; }
    public NativeThreadInfo[] Threads { get; }
    public int InvalidGroups { get; }
    public int PairedSpans { get; }
    public int UnmatchedEntries { get; }
    public int UnmatchedCompletions { get; }
    public bool Incomplete { get; }
    private struct Pair { public int Entry, Exit; public bool Invalid; }
    private sealed class Totals
    {
        public int Complete, Unwound, MissingEntry, MissingExit;
        public double Ticks;
        public long FirstEvent;
    }
    public NativeCaptureIndex(TraceView trace)
    {
        Process = trace.NativeMetadata.GetValueOrDefault((1, 0)) as NativeProcessInfo;
        End = trace.NativeMetadata.GetValueOrDefault((4, 0)) as NativeCaptureEnd;
        Control = trace.NativeMetadata.GetValueOrDefault((5, 0)) as NativeControlState;
        Threads = trace.NativeMetadata.Values.OfType<NativeThreadInfo>().OrderBy(t => t.Slot).ToArray();
        var slots = Threads.Select(t => t.Slot).ToHashSet();
        var statuses = trace.NativeMetadata.Values.OfType<NativeMethodInfo>().ToDictionary(m => m.SiteId);
        var groups = new Dictionary<long, Pair>();
        for (var i = 0; i < trace.Events.Length; i++)
        {
            ref readonly var e = ref trace.Events[i];
            if (!NativeMethodEvent.IsEvent(e)) continue;
            if (!groups.TryGetValue(e.Data1, out var pair)) pair = new() { Entry = -1, Exit = -1 };
            var entry = e.Data0 == NativeMethodEvent.BeginCode;
            pair.Invalid |= !NativeMethodEvent.IsValid(e) || (entry ? pair.Entry : pair.Exit) >= 0;
            if (entry) pair.Entry = i; else pair.Exit = i;
            groups[e.Data1] = pair;
        }
        var nativeEventCount = groups.Values.Sum(p => (p.Entry < 0 ? 0 : 1) + (p.Exit < 0 ? 0 : 1));
        var totals = new Dictionary<int, Totals>();
        foreach (var pair in groups.Values)
        {
            ref readonly var first = ref trace.Events[pair.Entry >= 0 ? pair.Entry : pair.Exit];
            var invalid = pair.Invalid || !trace.SourceSites.ContainsKey(first.NameId) || !slots.Contains(first.ThreadId);
            if (pair.Entry >= 0 && pair.Exit >= 0)
            {
                ref readonly var exit = ref trace.Events[pair.Exit];
                invalid |= first.Timestamp != exit.Timestamp || first.NameId != exit.NameId || first.ParentId != exit.ParentId || first.ThreadId != exit.ThreadId;
            }
            if (first.ParentId != 0 && groups.TryGetValue(first.ParentId >> 1, out var parent))
            {
                ref readonly var outer = ref trace.Events[parent.Entry >= 0 ? parent.Entry : parent.Exit];
                invalid |= first.Timestamp < outer.Timestamp || first.ThreadId != outer.ThreadId ||
                    pair.Exit >= 0 && parent.Exit >= 0 && trace.Events[pair.Exit].End > trace.Events[parent.Exit].End;
            }
            if (invalid) InvalidGroups++;
            if (!totals.TryGetValue(first.NameId, out var sum)) totals[first.NameId] = sum = new();
            if (sum.FirstEvent == 0) sum.FirstEvent = pair.Exit >= 0 ? trace.Events[pair.Exit].Id : first.Id;
            if (pair.Entry < 0) { UnmatchedCompletions++; sum.MissingEntry++; }
            if (pair.Exit < 0) { UnmatchedEntries++; sum.MissingExit++; }
            if (!invalid && pair.Entry >= 0 && pair.Exit >= 0)
            {
                ref readonly var exit = ref trace.Events[pair.Exit];
                PairedSpans++; sum.Complete++; sum.Ticks += exit.Duration;
                if (exit.Data0 == NativeMethodEvent.UnwoundCode) sum.Unwound++;
            }
        }
        Methods = trace.SourceSites.Keys.Union(statuses.Keys).Union(totals.Keys).Order().Select(id =>
        {
            statuses.TryGetValue(id, out var status); trace.SourceSites.TryGetValue(id, out var site);
            var sum = totals.GetValueOrDefault(id) ?? new();
            return new NativeMethodRow(id, site?.Assembly ?? "Unavailable", site?.Signature ?? $"Method #{id}", site?.Identity ?? "Unavailable",
                Status(status?.Status), status is { HResult: not 0 } ? $"0x{unchecked((uint)status.HResult):X8}" : "—", status?.Compilations ?? 0,
                sum.Complete, sum.Unwound, sum.MissingExit, sum.MissingEntry, sum.Ticks * 1000 / trace.Frequency, sum.FirstEvent);
        }).ToArray();
        Incomplete = Process is null || End is null || !End.HasFooter || End.TruncatedTail || End.Reason is 2 or 3 or 7 || End.Records != nativeEventCount ||
            End.DroppedRecords != 0 || End.RecorderFaults != 0 || End.IoFaults != 0 || End.RejitErrors != 0 || End.OpenFrames != 0 ||
            trace.DroppedEvents != 0 || trace.ProbeFaults != 0 || trace.MetadataDrops != 0 || trace.MissingBlocks != 0 || trace.EvictedEvents != 0 ||
            statuses.Values.Any(s => s.Status < 0) || InvalidGroups != 0 || UnmatchedEntries != 0 || UnmatchedCompletions != 0;
    }
    public static string Status(int? status) => status switch
    {
        0 => "Discovered", 1 => "ReJIT requested", 2 => "IL supplied", 3 => "Reverted for future calls", -8 => "Revert failed", -1 => "Not eligible IL", -2 => "Request failed", -3 => "ReJIT failed", -4 => "Rewrite failed",
        -5 => "Unsupported shape/budget", -6 => "IL/map submission failed", -7 => "Contained native failure", _ => "Status unavailable"
    };
}
