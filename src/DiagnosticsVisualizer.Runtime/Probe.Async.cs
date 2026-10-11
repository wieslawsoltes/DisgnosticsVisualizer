using System.Diagnostics;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

/// <summary>
/// Stored in the generated state machine. Contains no application/task/exception references
/// and only a weak session reference, so an unfinished operation cannot retain capture buffers.
/// </summary>
public struct AsyncOperationToken
{
    internal int State;
    internal WeakReference<CaptureSession>? Session;
    internal long Id, ObjectId;
    internal int NameId;
    internal Subsystem Subsystem;
}

public static partial class Probe
{
    /// <summary>
    /// Injection ABI, called at MoveNext entry. Even disabled first entries are remembered:
    /// enabling recording midway through an await must not invent a new operation start.
    /// </summary>
    public static void AsyncBegin(ref AsyncOperationToken token, int subsystem, string site, object? target)
    {
        if (Volatile.Read(ref token.State) != 0 || Interlocked.CompareExchange(ref token.State, 1, 0) != 0) return;
        var session = Current;
        if (session is null || !session.Recording || !session.Options.TrackAsyncOperations ||
            _insideProbe || _suppressionDepth != 0 || (uint)subsystem > (uint)Subsystem.Diagnostics ||
            (session.Options.SubsystemMask & (1UL << subsystem)) == 0) return;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return; }
            if (++writer.Samples % session.Options.SampleEvery != 0) return;
            token.Id = writer.NextId(); token.NameId = session.Intern(writer, site);
            token.ObjectId = session.GetObjectId(target); token.Subsystem = (Subsystem)subsystem;
            token.Session = new(session);
            var e = new TraceEvent(token.Id, LogicalParent(session, writer), Stopwatch.GetTimestamp(), 0,
                token.ObjectId, 0, writer.ThreadId, token.NameId, token.Subsystem,
                TraceEventKind.Link, AsyncOperationEvent.BeginCode);
            if (!writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }

    /// <summary>
    /// Injection ABI, immediately before the standard builder's terminal transition. Does not
    /// await, replace, consume, inspect Result/Exception, or attach a continuation to an awaitable.
    /// Completion of a captured start is retained during pause, but never crosses session identity.
    /// </summary>
    public static void AsyncComplete(ref AsyncOperationToken token, Exception? error)
    {
        if (Interlocked.Exchange(ref token.State, 2) != 1) return;
        var weak = token.Session; token.Session = null;
        if (weak is null || !weak.TryGetTarget(out var session) || !ReferenceEquals(session, Current)) return;
        if (_insideProbe) { session.Drop(); return; }
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return; }
            var outcome = error is OperationCanceledException ? AsyncOperationOutcome.Canceled :
                error is null ? AsyncOperationOutcome.Succeeded : AsyncOperationOutcome.Faulted;
            var e = new TraceEvent(writer.NextId(), token.Id, Stopwatch.GetTimestamp(), 0,
                token.ObjectId, 0, writer.ThreadId, token.NameId, token.Subsystem,
                TraceEventKind.Link, AsyncOperationEvent.EndCode, (long)outcome);
            if (!writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
}
