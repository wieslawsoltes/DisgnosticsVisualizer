using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

/// <summary>One producer per managed thread; one drain consumer. Publish indices establish visibility.</summary>
internal sealed class CaptureWriter(int ordinal, int capacity)
{
    private readonly TraceEvent[] _events = new TraceEvent[capacity];
    private readonly int _mask = capacity - 1;
    private long _written;
    private long _read;
    private long _sequence;
    public readonly int ThreadId = Environment.CurrentManagedThreadId;
    public readonly Dictionary<string, int> Symbols = new(StringComparer.Ordinal);
    public long Parent;
    public long Samples;
    public long NextId() => ((long)ordinal << 48) | (++_sequence & 0x0000ffffffffffffL);
    public bool TryWrite(in TraceEvent value)
    {
        var written = _written;
        if (written - Volatile.Read(ref _read) >= _events.Length) return false;
        _events[(int)written & _mask] = value;
        Volatile.Write(ref _written, written + 1); return true;
    }
    public void Drain(List<TraceEvent> target, int limit)
    {
        var read = _read; var written = Volatile.Read(ref _written);
        while (read != written && target.Count < limit) target.Add(_events[(int)read++ & _mask]);
        Volatile.Write(ref _read, read);
    }
}
