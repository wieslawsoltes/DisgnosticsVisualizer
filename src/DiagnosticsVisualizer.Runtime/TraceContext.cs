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
            var writer = session.Writer;
            var parent = writer is null ? 0 : Probe.LogicalParent(session, writer);
            return parent == 0 ? default : new(session.Id, parent);
        }
        catch { session.Fault(); return default; }
    }
    /// <summary>
    /// Resume a recorded causal parent for a synchronous continuation. The token can be
    /// stored in a queued work item; this returned lease must stay on the executing thread.
    /// Ordinary probes remain allocation-free unless FlowExecutionContext is enabled.
    /// </summary>
    public TraceContextLease Resume()
    {
        var session = Probe.Current;
        if (IsEmpty || session?.Recording != true || session.Id != SessionId) return default;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return default; }
            var previous = writer.Parent;
            var flow = session.Options.FlowExecutionContext ? Probe.Ambient.Value : default;
            if (session.Options.FlowExecutionContext) Probe.Ambient.Value = this;
            writer.Parent = ParentEventId;
            return new(session, writer, previous, ParentEventId, flow);
        }
        catch { session.Fault(); return default; }
    }
}

public readonly ref struct TraceContextLease
{
    private readonly CaptureSession? _session;
    private readonly CaptureWriter? _writer;
    private readonly long _previous, _expected;
    private readonly TraceContext _flow;
    internal TraceContextLease(CaptureSession session, CaptureWriter writer, long previous, long expected, TraceContext flow)
    { _flow = flow; _session = session; _writer = writer; _previous = previous; _expected = expected; }
    public void Dispose()
    {
        if (_session is null || _writer is null) return;
        if (_writer.ThreadId != Environment.CurrentManagedThreadId || _writer.Parent != _expected)
        { _session.Fault(); return; }
        if (_session.Options.FlowExecutionContext) Probe.Ambient.Value = _flow;
        _writer.Parent = _previous;
    }
}
