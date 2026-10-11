namespace DiagnosticsVisualizer.Runtime;

public static partial class Probe
{
    // No change callback: switching ExecutionContext must never mutate another thread's
    // synchronous stack. Ambient state is authoritative only in the opt-in mode.
    internal static readonly AsyncLocal<TraceContext> Ambient = new();

    internal static long LogicalParent(CaptureSession session, CaptureWriter writer)
    {
        if (!session.Options.FlowExecutionContext) return writer.Parent;
        var flow = Ambient.Value;
        return flow.SessionId == session.Id ? flow.ParentEventId : 0;
    }
}
