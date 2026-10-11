using System.Diagnostics;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal sealed class DispatcherCapture(long id, long objectId, int nameId, int inactivePriority)
{
    public readonly long Id = id, ObjectId = objectId;
    public readonly int InactivePriority = inactivePriority;
    public int NameId = nameId;
    public int Phase, Threw;
}

public static partial class Probe
{
    /// <summary>Called after queue admission, while the framework still owns its queue lock.</summary>
    public static void DispatcherQueued(object? operation, int priority, int inactivePriority, string site)
    {
        var session = Current;
        if (operation is null || session is not { Recording: true, Options.TrackDispatcherOperations: true } ||
            _insideProbe || _suppressionDepth != 0 || (session.Options.SubsystemMask & (1UL << (int)Subsystem.Dispatcher)) == 0) return;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return; }
            if (++writer.Samples % session.Options.SampleEvery != 0) return;
            var objectId = session.GetObjectId(operation);
            if (objectId == 0) return; // The same metadata budget bounds weak queue state.
            var capture = new DispatcherCapture(writer.NextId(), objectId, session.Intern(writer, site), inactivePriority);
            lock (session.Dispatchers)
            {
                if (session.Dispatchers.TryGetValue(operation, out _)) return;
                session.Dispatchers.Add(operation, capture);
            }
            var e = new TraceEvent(capture.Id, LogicalParent(session, writer), Stopwatch.GetTimestamp(), 0,
                objectId, 0, writer.ThreadId, capture.NameId, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.QueuedCode, DispatcherEvent.PackPriority(priority, priority > inactivePriority));
            if (!writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }

    /// <summary>Called after a queued operation changes priority. Does not sample away transitions of a captured admission.</summary>
    public static void DispatcherPriorityChanged(object operation, int priority)
    {
        var session = Current;
        if (session is null || _insideProbe) return;
        _insideProbe = true;
        try
        {
            if (!session.Dispatchers.TryGetValue(operation, out var capture) || Volatile.Read(ref capture.Phase) != 0) return;
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return; }
            var e = new TraceEvent(writer.NextId(), capture.Id, Stopwatch.GetTimestamp(), 0, capture.ObjectId, 0,
                writer.ThreadId, capture.NameId, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.PriorityCode,
                DispatcherEvent.PackPriority(priority, priority > capture.InactivePriority));
            if (!writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }

    /// <summary>Called inside the framework's execution context, immediately outside InvokeCore.</summary>
    public static DispatcherExecutionScope DispatcherStarted(object operation, int priority, string site)
    {
        var session = Current;
        if (session is null || _insideProbe || _suppressionDepth != 0) return default;
        _insideProbe = true;
        try
        {
            if (!session.Dispatchers.TryGetValue(operation, out var capture) || Interlocked.CompareExchange(ref capture.Phase, 1, 0) != 0) return default;
            var writer = session.Writer;
            if (writer is null) { session.Drop(); session.Dispatchers.Remove(operation); return default; }
            capture.NameId = session.Intern(writer, site); var id = writer.NextId();
            var e = new TraceEvent(id, capture.Id, Stopwatch.GetTimestamp(), 0, capture.ObjectId, 0,
                writer.ThreadId, capture.NameId, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.StartedCode, DispatcherEvent.PackPriority(priority, priority > capture.InactivePriority));
            if (!writer.TryWrite(e)) session.Drop();
            // Explicit context also connects Post paths that intentionally do not flow ExecutionContext.
            var context = new TraceContext(session.Id, id).Resume();
            return new(session, operation, capture, context);
        }
        catch { session.Fault(); return default; }
        finally { _insideProbe = false; }
    }

    /// <summary>Observes the framework's caught callback exception without retaining it or changing task state.</summary>
    public static void DispatcherFault(Exception? error, object operation)
    {
        var session = Current;
        if (session is null || error is null) return;
        try { if (session.Dispatchers.TryGetValue(operation, out var capture)) Volatile.Write(ref capture.Threw, 1); }
        catch { session.Fault(); }
    }

    public static void DispatcherAborted(object operation)
    {
        var session = Current;
        if (session is null || _insideProbe) return;
        _insideProbe = true;
        try
        {
            if (!session.Dispatchers.TryGetValue(operation, out var capture) || Interlocked.CompareExchange(ref capture.Phase, 3, 0) != 0) return;
            session.Dispatchers.Remove(operation);
            WriteDispatcherEnd(session, capture, DispatcherEvent.AbortedCode, DispatcherOutcome.Aborted);
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }

    internal static void DispatcherFinished(CaptureSession session, object operation, DispatcherCapture capture)
    {
        if (!ReferenceEquals(Current, session) || _insideProbe) return;
        _insideProbe = true;
        try
        {
            session.Dispatchers.Remove(operation);
            WriteDispatcherEnd(session, capture, DispatcherEvent.FinishedCode,
                Volatile.Read(ref capture.Threw) == 0 ? DispatcherOutcome.Returned : DispatcherOutcome.Threw);
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    private static void WriteDispatcherEnd(CaptureSession session, DispatcherCapture capture, long code, DispatcherOutcome outcome)
    {
        var writer = session.Writer;
        if (writer is null) { session.Drop(); return; }
        var e = new TraceEvent(writer.NextId(), capture.Id, Stopwatch.GetTimestamp(), 0, capture.ObjectId, 0,
            writer.ThreadId, capture.NameId, Subsystem.Dispatcher, TraceEventKind.Link, code, (long)outcome);
        if (!writer.TryWrite(e)) session.Drop();
    }
}

/// <summary>Thread-affine callback scope. No reference to the work item survives its synchronous execution.</summary>
public readonly ref struct DispatcherExecutionScope
{
    private readonly CaptureSession? _session;
    private readonly object? _operation;
    private readonly DispatcherCapture? _capture;
    private readonly TraceContextLease _context;
    internal DispatcherExecutionScope(CaptureSession session, object operation, DispatcherCapture capture, TraceContextLease context)
    { _session = session; _operation = operation; _capture = capture; _context = context; }
    public void Dispose()
    {
        if (_session is null || _operation is null || _capture is null || Interlocked.CompareExchange(ref _capture.Phase, 2, 1) != 1) return;
        try { _context.Dispose(); } catch { _session.Fault(); }
        Probe.DispatcherFinished(_session, _operation, _capture);
    }
}
