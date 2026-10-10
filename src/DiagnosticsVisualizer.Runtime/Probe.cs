using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

/// <summary>Fail-contained injection ABI. No I/O or UI access on a producer thread.</summary>
public static class Probe
{
    private static CaptureSession? _current;
    [ThreadStatic] private static bool _insideProbe;
    [ThreadStatic] private static int _suppressionDepth;
    public static CaptureSession? Current => Volatile.Read(ref _current);
    public static CaptureSession Start(CaptureOptions? options = null)
    {
        var session = new CaptureSession(options);
        if (Interlocked.CompareExchange(ref _current, session, null) is not null)
        { session.Dispose(); throw new InvalidOperationException("A capture session is already attached."); }
        return session;
    }
    internal static void Detach(CaptureSession session) => Interlocked.CompareExchange(ref _current, null, session);

    /// <summary>Exclude synchronous diagnostic work itself. Never hold this lease across an await.</summary>
    public static ProbeSuppression Suppress() => new(++_suppressionDepth, Environment.CurrentManagedThreadId);
    internal static void EndSuppression(int depth, int thread)
    {
        if (Environment.CurrentManagedThreadId != thread || _suppressionDepth != depth)
        { Current?.Fault(); return; }
        _suppressionDepth--;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TraceScope Enter(int subsystem, string site, object? target, object? related = null)
    {
        var session = Volatile.Read(ref _current);
        if (session is null || !session.Recording || _insideProbe || _suppressionDepth != 0 ||
            (uint)subsystem > (uint)Subsystem.Diagnostics || (session.Options.SubsystemMask & (1UL << subsystem)) == 0) return default;
        return EnterSlow(session, (Subsystem)subsystem, site, target, related);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TraceScope EnterSlow(CaptureSession session, Subsystem subsystem, string site, object? target, object? related)
    {
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return default; }
            if (++writer.Samples % session.Options.SampleEvery != 0) return default;
            var name = session.Intern(writer, site); var obj = session.GetObjectId(target); var relation = session.GetObjectId(related);
            var id = writer.NextId(); var parent = writer.Parent; writer.Parent = id;
            return new(session, writer, id, parent, Stopwatch.GetTimestamp(), obj, relation, name, subsystem);
        }
        catch { session.Fault(); return default; }
        finally { _insideProbe = false; }
    }
    internal static void Exit(CaptureSession session, CaptureWriter writer, long id, long parent,
        long start, long obj, long related, int name, Subsystem subsystem)
    {
        if (_insideProbe) return;
        _insideProbe = true;
        try
        {
            // Copied, doubly-disposed or out-of-order scopes must not corrupt the caller's parent stack.
            if (writer.ThreadId != Environment.CurrentManagedThreadId || writer.Parent != id)
            { session.Fault(); return; }
            writer.Parent = parent;
            var e = new TraceEvent(id, parent, start, Math.Max(0, Stopwatch.GetTimestamp() - start),
                obj, related, writer.ThreadId, name, subsystem);
            if (!ReferenceEquals(Current, session) || !writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    public static void Instant(Subsystem subsystem, string name, object? target = null,
        object? related = null, long data0 = 0, long data1 = 0, TraceEventKind kind = TraceEventKind.Instant)
    {
        var session = Current;
        if (session is null || !session.Recording || _insideProbe || _suppressionDepth != 0 ||
            (uint)subsystem > (uint)Subsystem.Diagnostics || (uint)kind > (uint)TraceEventKind.Link ||
            (session.Options.SubsystemMask & (1UL << (int)subsystem)) == 0) return;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return; }
            var e = new TraceEvent(writer.NextId(), writer.Parent, Stopwatch.GetTimestamp(), 0,
                session.GetObjectId(target), session.GetObjectId(related), writer.ThreadId,
                session.Intern(writer, name), subsystem, kind, data0, data1);
            if (!writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    // Injected immediately before delegate invocation. Null event arguments are legal IL;
    // observing them must not introduce a new exception into the application.
    public static void PropertyNotification(object? sender, PropertyChangedEventArgs? args)
    {
        if (Current?.Recording != true) return;
        Instant(Subsystem.PropertyNotifications, args?.PropertyName ?? "*", sender);
    }
    public static void CollectionNotification(object? sender, NotifyCollectionChangedEventArgs? args)
    {
        if (Current?.Recording != true) return;
        var name = args?.Action switch
        {
            NotifyCollectionChangedAction.Add => "Add",
            NotifyCollectionChangedAction.Remove => "Remove",
            NotifyCollectionChangedAction.Replace => "Replace",
            NotifyCollectionChangedAction.Move => "Move",
            NotifyCollectionChangedAction.Reset => "Reset",
            _ => "Unknown"
        };
        Instant(Subsystem.CollectionNotifications, name, sender, null,
            args?.NewStartingIndex ?? -1, args?.OldStartingIndex ?? -1);
    }
}

/// <summary>A thread-affine, nestable suppression lease; misuse is reported, not thrown into target code.</summary>
public readonly ref struct ProbeSuppression
{
    private readonly int _depth, _thread;
    internal ProbeSuppression(int depth, int thread) { _depth = depth; _thread = thread; }
    public void Dispose() { if (_depth != 0) Probe.EndSuppression(_depth, _thread); }
}

/// <summary>Synchronous scope: ref-like to prevent accidentally retaining it across await.</summary>
public readonly ref struct TraceScope
{
    private readonly CaptureSession? _session;
    private readonly CaptureWriter? _writer;
    private readonly long _id, _parent, _start, _object, _related;
    private readonly int _name;
    private readonly Subsystem _subsystem;
    internal TraceScope(CaptureSession session, CaptureWriter writer, long id, long parent,
        long start, long obj, long related, int name, Subsystem subsystem)
    { _session = session; _writer = writer; _id = id; _parent = parent; _start = start;
      _object = obj; _related = related; _name = name; _subsystem = subsystem; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (_session is not null && _writer is not null)
            Probe.Exit(_session, _writer, _id, _parent, _start, _object, _related, _name, _subsystem);
    }
}
