namespace DiagnosticsVisualizer.Core;

public enum DispatcherOutcome { Returned = 1, Threw = 2, Aborted = 3 }
public static class DispatcherEvent
{
    public const long QueuedCode = 0x44535001;
    public const long StartedCode = 0x44535002;
    public const long FinishedCode = 0x44535003;
    public const long AbortedCode = 0x44535004;
    public const long PriorityCode = 0x44535005;
    public static long PackPriority(int priority, bool runnable) => (uint)priority | (runnable ? 0L : 1L << 32);
    public static bool IsValidPriority(long value) => (value & ~0x1ffffffffL) == 0;
    public static int Priority(long value) => unchecked((int)value);
    public static bool IsRunnable(long value) => (value & (1L << 32)) == 0;
    public static bool IsMarker(in TraceEvent e) => e.Subsystem == Subsystem.Dispatcher && e.Kind == TraceEventKind.Link &&
        e.Data0 is QueuedCode or StartedCode or FinishedCode or AbortedCode or PriorityCode;
}
