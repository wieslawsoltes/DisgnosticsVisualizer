namespace DiagnosticsVisualizer.Runtime;

/// <summary>A value token that may cross threads/await; it never keeps a target object alive.</summary>
public readonly record struct TraceContext(Guid SessionId, long ParentEventId)
{
    public bool IsEmpty => SessionId == Guid.Empty || ParentEventId == 0;
    public static TraceContext Capture()
    {
        var session = Probe.Current;
        if (session?.Recording != true) return default;
        try
        {
            var parent = session.Writer?.Parent ?? 0;
            return parent == 0 ? default : new(session.Id, parent);
        }
        catch { session.Fault(); return default; }
    }
    /// <summary>
    /// Resume a recorded causal parent for a synchronous continuation. The token can be
    /// stored in a queued work item; this returned lease must stay on the executing thread.
    /// No AsyncLocal or ExecutionContext allocation is added to ordinary probes.
    /// </summary>
    public TraceContextLease Resume()
    {
        var session = Probe.Current;
        if (IsEmpty || session?.Recording != true || session.Id != SessionId) return default;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return default; }
            var previous = writer.Parent; writer.Parent = ParentEventId;
            return new(session, writer, previous, ParentEventId);
        }
        catch { session.Fault(); return default; }
    }
}

public readonly ref struct TraceContextLease
{
    private readonly CaptureSession? _session;
    private readonly CaptureWriter? _writer;
    private readonly long _previous, _expected;
    internal TraceContextLease(CaptureSession session, CaptureWriter writer, long previous, long expected)
    { _session = session; _writer = writer; _previous = previous; _expected = expected; }
    public void Dispose()
    {
        if (_session is null || _writer is null) return;
        if (_writer.ThreadId != Environment.CurrentManagedThreadId || _writer.Parent != _expected)
        { _session.Fault(); return; }
        _writer.Parent = _previous;
    }
}
