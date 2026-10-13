using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

/// <summary>Caller owns the current graphics context. Methods must not block for query completion.</summary>
public interface IGpuTimestampBackend
{
    GpuTimingSite Site { get; }
    bool IsLost { get; }
    bool CanRead { get; }
    uint CreateQuery();
    void DeleteQuery(uint query);
    void Timestamp(uint query);
    bool TryRead(uint query, out ulong nanoseconds);
}

/// <summary>A generation-checked handle. Copies cannot complete a later reuse of the same slot.</summary>
public readonly struct GpuSubmission
{
    internal readonly GpuTimestampArena? Arena;
    internal readonly int Slot;
    internal readonly long Generation;
    internal GpuSubmission(GpuTimestampArena arena, int slot, long generation)
    { Arena = arena; Slot = slot; Generation = generation; }
    public bool IsValid => Arena is not null;
    public void Submit() => Arena?.Submit(this);
    public void Abort() => Arena?.Abort(this);
}

/// <summary>
/// Bounded, thread-affine query pairs. Poll is budgeted and never requests an unavailable result.
/// A timeout disables this arena rather than recycling in-flight query objects indefinitely.
/// </summary>
public sealed class GpuTimestampArena : IDisposable
{
    private struct Entry
    {
        public uint Start, End;
        public int State, Polls; // 0 free, 1 submitting, 2 waiting
        public long Generation;
        public GpuCaptureToken Capture;
    }
    private readonly IGpuTimestampBackend _backend;
    private readonly Entry[] _entries;
    private readonly object _context;
    private readonly string _descriptor;
    private readonly int _thread = Environment.CurrentManagedThreadId, _pollBudget, _maximumPolls;
    private int _cursor;
    private bool _closed, _deleted;
    private GpuQueryOutcome _closedReason;
    public int Capacity => _entries.Length;
    public int PendingCount { get; private set; }
    public GpuTimestampArena(IGpuTimestampBackend backend, object context, int capacity = 32, int pollBudget = 8, int maximumPolls = 600)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(context);
        if (capacity is < 1 or > 256 || pollBudget < 1 || pollBudget > capacity || maximumPolls is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _backend = backend; _context = context; _pollBudget = pollBudget; _maximumPolls = maximumPolls;
        _descriptor = GpuTimingSymbol.Encode(backend.Site); _entries = new Entry[capacity];
        // No GL calls here. Unsupported contexts and disabled recordings must not allocate queries.
    }
    public GpuSubmission Begin()
    {
        if (Environment.CurrentManagedThreadId != _thread) return default;
        Poll();
        var capture = Probe.GpuBegin(_descriptor, _context);
        if (!capture.IsValid) return default;
        if (_closed) { Probe.GpuComplete(capture, _closedReason); return default; }
        if (_backend.Site.CounterBits != 64) { Probe.GpuComplete(capture, GpuQueryOutcome.Unsupported); return default; }
        var admitted = false;
        try
        {
            if (_backend.IsLost) { Probe.GpuComplete(capture, GpuQueryOutcome.ContextLost); Abandon(GpuQueryOutcome.ContextLost); return default; }
            var index = Array.FindIndex(_entries, e => e.State == 0);
            if (index < 0) { Probe.GpuComplete(capture, GpuQueryOutcome.CapacityExceeded); return default; }
            ref var entry = ref _entries[index];
            if (entry.Start == 0) entry.Start = _backend.CreateQuery();
            if (entry.End == 0) entry.End = _backend.CreateQuery();
            if (entry.Start == 0 || entry.End == 0) throw new InvalidOperationException("GPU query allocation failed.");
            entry.Capture = capture; entry.State = 1; entry.Polls = 0; entry.Generation++;
            PendingCount++; admitted = true;
            _backend.Timestamp(entry.Start);
            return new(this, index, entry.Generation);
        }
        catch { if (!admitted) Probe.GpuComplete(capture, GpuQueryOutcome.BackendFault); Abandon(GpuQueryOutcome.BackendFault); return default; }
    }
    internal void Submit(in GpuSubmission submission)
    {
        if (!TryGet(submission, out var index)) return;
        ref var entry = ref _entries[index];
        if (entry.State != 1) return;
        try
        {
            if (_backend.IsLost) { Abandon(GpuQueryOutcome.ContextLost); return; }
            _backend.Timestamp(entry.End);
            Probe.GpuSubmitted(entry.Capture); entry.State = 2;
        }
        catch { Abandon(GpuQueryOutcome.BackendFault); }
    }
    internal void Abort(in GpuSubmission submission)
    {
        if (TryGet(submission, out var index) && _entries[index].State == 1) Finish(index, GpuQueryOutcome.SubmissionAborted);
    }
    private bool TryGet(in GpuSubmission submission, out int index)
    {
        index = submission.Slot;
        return !_closed && Environment.CurrentManagedThreadId == _thread && ReferenceEquals(submission.Arena, this) &&
            (uint)index < (uint)_entries.Length && _entries[index].Generation == submission.Generation;
    }
    public void Poll()
    {
        if (_closed || PendingCount == 0 || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (_backend.IsLost) { Abandon(GpuQueryOutcome.ContextLost); return; }
            var budget = _pollBudget;
            for (var visited = 0; visited < _entries.Length && budget > 0; visited++)
            {
                var index = _cursor; _cursor = (_cursor + 1) % _entries.Length;
                ref var entry = ref _entries[index];
                if (entry.State != 2) continue;
                budget--;
                if (++entry.Polls > _maximumPolls) { Abandon(GpuQueryOutcome.PendingLimit); return; }
                if (!_backend.CanRead || !_backend.TryRead(entry.End, out var end) || !_backend.TryRead(entry.Start, out var start)) continue;
                if (end < start) { Finish(index, GpuQueryOutcome.InvalidClock); continue; }
                Probe.GpuCounters(entry.Capture, start, end); Finish(index, GpuQueryOutcome.Available);
            }
        }
        catch { Abandon(GpuQueryOutcome.BackendFault); }
    }
    private void Finish(int index, GpuQueryOutcome outcome)
    {
        ref var entry = ref _entries[index];
        if (entry.State == 0) return;
        Probe.GpuComplete(entry.Capture, outcome); entry.Capture = default; entry.State = 0; PendingCount--;
    }
    /// <summary>Contains loss/disposal without graphics API calls. Query names are reclaimed with their context.</summary>
    public void Abandon(GpuQueryOutcome reason = GpuQueryOutcome.ContextLost)
    {
        if (Environment.CurrentManagedThreadId != _thread || _closed) return;
        if (reason is GpuQueryOutcome.Available or GpuQueryOutcome.CapacityExceeded) throw new ArgumentOutOfRangeException(nameof(reason));
        _closed = true; _closedReason = reason;
        for (var i = 0; i < _entries.Length; i++) Finish(i, reason);
    }
    /// <summary>Call on the owning thread with its original graphics context current. Never waits for completion.</summary>
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _thread || _deleted) return;
        Abandon(GpuQueryOutcome.ContextDisposed); _deleted = true;
        try
        {
            if (_backend.IsLost) return;
            foreach (var entry in _entries)
            { if (entry.Start != 0) _backend.DeleteQuery(entry.Start); if (entry.End != 0) _backend.DeleteQuery(entry.End); }
        }
        catch { /* Context destruction reclaims any remaining names. */ }
    }
}
