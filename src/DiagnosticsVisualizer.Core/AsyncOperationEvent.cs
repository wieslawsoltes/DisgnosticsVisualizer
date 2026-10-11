namespace DiagnosticsVisualizer.Core;

/// <summary>
/// Wire-compatible async lifecycle markers. Begin.Id is the operation identity; End.ParentId
/// refers to it. Marker timestamps bracket state-machine execution, including suspended time.
/// They are links, never synchronous spans, and must not contribute to CPU/self-time costs.
/// </summary>
public static class AsyncOperationEvent
{
    public const long BeginCode = 0x41564153594E4301;
    public const long EndCode = 0x41564153594E4302;
    public static bool IsBegin(in TraceEvent e) => e.Kind == TraceEventKind.Link && e.Data0 == BeginCode;
    public static bool IsEnd(in TraceEvent e) => e.Kind == TraceEventKind.Link && e.Data0 == EndCode;
}

public enum AsyncOperationOutcome : byte { Succeeded = 1, Faulted = 2, Canceled = 3 }
