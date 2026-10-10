using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

/// <summary>Small, fail-contained injection ABI. No formatting, I/O, or UI access on the hot path.</summary>
public static class Probe
{
    private static CaptureSession? _current;
    [ThreadStatic] private static bool _insideProbe;
    public static CaptureSession? Current => Volatile.Read(ref _current);
    public static CaptureSession Start(CaptureOptions? options = null)
    {
        var session = new CaptureSession(options);
        if (Interlocked.CompareExchange(ref _current, session, null) is not null)
        { session.Dispose(); throw new InvalidOperationException("A capture session is already attached."); }
        return session;
    }
    internal static void Detach(CaptureSession session) => Interlocked.CompareExchange(ref _current, null, session);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TraceScope Enter(int subsystem, string site, object? target, object? related = null)
    {
        var session = Volatile.Read(ref _current);
        if (session is null || !session.Recording || _insideProbe || (uint)subsystem > (uint)Subsystem.Diagnostics ||
            (session.Options.SubsystemMask & (1UL << subsystem)) == 0) return default;
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
            if (writer.ThreadId != Environment.CurrentManagedThreadId) { session.Fault(); return; }
            writer.Parent = parent;
            var e = new TraceEvent(id, parent, start, Math.Max(0, Stopwatch.GetTimestamp() - start),
                obj, related, writer.ThreadId, name, subsystem);
            // Closing spans are retained while paused; Dispose ends the session and detaches it.
            if (!ReferenceEquals(Current, session) || !writer.TryWrite(e)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    public static void Instant(Subsystem subsystem, string name, object? target = null,
        object? related = null, long data0 = 0, long data1 = 0, TraceEventKind kind = TraceEventKind.Instant)
    {
        var session = Current;
        if (session is null || !session.Recording || _insideProbe ||
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
    // These methods are inserted immediately before actual delegate invocation sites.
    public static void PropertyNotification(object? sender, PropertyChangedEventArgs args)
    {
        if (Current?.Recording != true) return;
        Instant(Subsystem.PropertyNotifications, args.PropertyName ?? "*", sender);
    }
    public static void CollectionNotification(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (Current?.Recording != true) return;
        Instant(Subsystem.CollectionNotifications, args.Action.ToString(), sender, null,
            args.NewStartingIndex, args.OldStartingIndex);
    }
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
