namespace DiagnosticsVisualizer.Analysis;

/// <summary>
/// Bounded out-of-order sequence accounting. Gaps below the retired replay horizon remain
/// counted, but a delayed packet below that horizon can no longer repair them.
/// </summary>
internal sealed class TraceSequenceLedger(int capacity)
{
    private readonly SortedSet<long> _active = [];
    private long _retiredThrough, _retiredMissing;
    public long Highest { get; private set; }
    public long Missing => _active.Count == 0 ? _retiredMissing : _retiredThrough == 0
        ? Highest - _active.Min - (_active.Count - 1)
        : _retiredMissing + (Highest - _retiredThrough - _active.Count);

    public bool TryAccept(long sequence)
    {
        if (sequence <= 0) throw new InvalidDataException("Trace sequence must be positive.");
        if (sequence <= _retiredThrough || !_active.Add(sequence)) return false;
        Highest = Math.Max(Highest, sequence);
        if (_active.Count > capacity)
        {
            var oldest = _active.Min;
            // The first received range can start at an arbitrary sequence (a late-joining
            // viewer). Do not manufacture loss before the earliest retained observation.
            if (_retiredThrough != 0) _retiredMissing += oldest - _retiredThrough - 1;
            _retiredThrough = oldest;
            _active.Remove(oldest);
        }
        return true;
    }
}
