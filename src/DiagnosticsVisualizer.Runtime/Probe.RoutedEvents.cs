using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal readonly record struct RoutedContext(CaptureSession? Session, CaptureWriter? Writer, object? Args, long DispatchId, int NameId);
internal sealed record RoutedHandlerName(int NameId);

public static partial class Probe
{
    [ThreadStatic] private static RoutedContext _routedContext;
    [ThreadStatic] private static int _routedDepth;

    /// <summary>Every raise shadows its outer dispatch, including an unsampled nested raise with reused event arguments.</summary>
    public static RoutedDispatchScope BeginRoutedEvent(object args, object? source, object eventIdentity, string name, int routes, bool handled)
    {
        var previous = _routedContext;
        _routedContext = default;
        var depth = ++_routedDepth;
        var scope = new RoutedDispatchScope(previous, depth, Environment.CurrentManagedThreadId);
        var session = Current;
        if (session is not { Recording: true, Options.TrackRoutedEvents: true } || _insideProbe || _suppressionDepth != 0 ||
            (uint)routes > 7 || (session.Options.SubsystemMask & (1UL << (int)Subsystem.RoutedEvents)) == 0) return scope;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return scope; }
            if (++writer.Samples % session.Options.SampleEvery != 0) return scope;
            var sourceId = session.GetObjectId(source); var eventId = session.GetObjectId(eventIdentity);
            var nameId = session.Intern(writer, name); var id = writer.NextId();
            scope.Span = BeginRoutedSpan(session, writer, id, id, sourceId, eventId, nameId, RoutedStepKind.Dispatch, routes, handled, false);
            _routedContext = new(session, writer, args, id, nameId);
            var entry = new TraceEvent(writer.NextId(), id, scope.Span.Start, 0, sourceId, eventId, writer.ThreadId, nameId,
                Subsystem.RoutedEvents, TraceEventKind.Link, RoutedDispatchEvent.Pack(RoutedStepKind.Begin, routes, handled, handled, false, false), id);
            if (!writer.TryWrite(entry)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
        return scope;
    }

    public static void EndRoutedEvent(ref RoutedDispatchScope scope, bool handled, bool returned)
    {
        if (!scope.IsEntered) return;
        if (scope.Thread != Environment.CurrentManagedThreadId || scope.Depth != _routedDepth) { Current?.Fault(); return; }
        _routedContext = scope.Previous; _routedDepth--;
        EndRoutedHandler(ref scope.Span, handled, returned);
        scope = default;
    }

    /// <summary>Aggregate invocation boundary: one class-listener notification, one typed adapter, or one DynamicInvoke.</summary>
    public static RoutedHandlerScope BeginRoutedHandler(Delegate? handler, object? target, object args, object? source,
        int route, bool handled, bool handledEventsToo, bool classListeners)
    {
        var context = _routedContext;
        if (!ReferenceEquals(context.Args, args) || context.Session is not { } session || !ReferenceEquals(Current, session) ||
            context.Writer is not { } writer || _insideProbe || _suppressionDepth != 0 || route is not (1 or 2 or 4)) return default;
        _insideProbe = true;
        try
        {
            var nameId = handler is null ? context.NameId : HandlerName(session, writer, handler);
            return BeginRoutedSpan(session, writer, writer.NextId(), context.DispatchId, session.GetObjectId(target),
                session.GetObjectId(source), nameId, classListeners ? RoutedStepKind.ClassListeners : RoutedStepKind.Handler, route, handled, handledEventsToo);
        }
        catch { session.Fault(); return default; }
        finally { _insideProbe = false; }
    }

    public static void RoutedCandidate(Delegate? handler, object? target, object args, object? source, int route,
        int registeredRoutes, bool handled, bool handledEventsToo)
    {
        if (handler is null || ((registeredRoutes & route) == route && (!handled || handledEventsToo))) return;
        var context = _routedContext;
        if (!ReferenceEquals(context.Args, args) || context.Session is not { } session || !ReferenceEquals(Current, session) ||
            context.Writer is not { } writer || _insideProbe || _suppressionDepth != 0 || route is not (1 or 2 or 4)) return;
        _insideProbe = true;
        try
        {
            var kind = (registeredRoutes & route) != route ? RoutedStepKind.SkippedRoute : RoutedStepKind.SkippedHandled;
            var entry = new TraceEvent(writer.NextId(), LogicalParent(session, writer), Stopwatch.GetTimestamp(), 0,
                session.GetObjectId(target), session.GetObjectId(source), writer.ThreadId, HandlerName(session, writer, handler),
                Subsystem.RoutedEvents, TraceEventKind.Instant, RoutedDispatchEvent.Pack(kind, route, handled, handled, handledEventsToo, false), context.DispatchId);
            if (!writer.TryWrite(entry)) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }

    private static int HandlerName(CaptureSession session, CaptureWriter writer, Delegate handler)
    {
        if (session.RoutedHandlers.TryGetValue(handler, out var cached)) return cached.NameId;
        // Identity budgets also bound this weak metadata cache. Never retain a callback or its target.
        if (session.GetObjectId(handler) == 0) return 0;
        var methods = handler.GetInvocationList();
        var name = methods.Length == 1 ? $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}" :
            $"{handler.GetType().Name} multicast ({methods.Length} delegates)";
        var id = session.Intern(writer, name.Length <= 1024 ? name : name[..1024]);
        lock (session.RoutedHandlers)
        {
            if (!session.RoutedHandlers.TryGetValue(handler, out cached)) session.RoutedHandlers.Add(handler, new(id));
            else id = cached.NameId;
        }
        return id;
    }

    private static RoutedHandlerScope BeginRoutedSpan(CaptureSession session, CaptureWriter writer, long id, long dispatch,
        long target, long related, int name, RoutedStepKind kind, int route, bool handled, bool handledEventsToo)
    {
        var previous = writer.Parent; var parent = LogicalParent(session, writer);
        var flow = session.Options.FlowExecutionContext ? Ambient.Value : default;
        if (session.Options.FlowExecutionContext) Ambient.Value = new(session.Id, id);
        writer.Parent = id;
        return new(session, writer, id, parent, previous, flow, Stopwatch.GetTimestamp(), dispatch, target, related, name, kind, route, handled, handledEventsToo);
    }

    public static void EndRoutedHandler(ref RoutedHandlerScope scope, bool handled, bool returned)
    {
        if (scope.Session is not { } session || scope.Writer is not { } writer || _insideProbe) return;
        _insideProbe = true;
        try
        {
            if (writer.ThreadId != Environment.CurrentManagedThreadId || writer.Parent != scope.Id) { session.Fault(); return; }
            if (session.Options.FlowExecutionContext) Ambient.Value = scope.Flow;
            writer.Parent = scope.PreviousParent;
            if (!ReferenceEquals(Current, session)) return;
            var entry = new TraceEvent(scope.Id, scope.Parent, scope.Start, Math.Max(0, Stopwatch.GetTimestamp() - scope.Start),
                scope.Target, scope.Related, writer.ThreadId, scope.NameId, Subsystem.RoutedEvents, TraceEventKind.Span,
                RoutedDispatchEvent.Pack(scope.Kind, scope.Route, scope.HandledBefore, handled, scope.HandledEventsToo, returned), scope.DispatchId);
            if (!writer.TryWrite(entry)) session.Drop();
        }
        catch { session.Fault(); }
        finally { scope = default; _insideProbe = false; }
    }
}

public ref struct RoutedDispatchScope
{
    internal RoutedContext Previous;
    internal int Depth, Thread;
    internal RoutedHandlerScope Span;
    public readonly bool IsEntered => Depth != 0;
    internal RoutedDispatchScope(RoutedContext previous, int depth, int thread) { Previous = previous; Depth = depth; Thread = thread; }
}

public ref struct RoutedHandlerScope
{
    internal CaptureSession? Session;
    internal CaptureWriter? Writer;
    internal long Id, Parent, PreviousParent, Start, DispatchId, Target, Related;
    internal TraceContext Flow;
    internal int NameId, Route;
    internal RoutedStepKind Kind;
    internal bool HandledBefore, HandledEventsToo;
    internal RoutedHandlerScope(CaptureSession session, CaptureWriter writer, long id, long parent, long previous, TraceContext flow,
        long start, long dispatch, long target, long related, int name, RoutedStepKind kind, int route, bool handled, bool too)
    {
        Session = session; Writer = writer; Id = id; Parent = parent; PreviousParent = previous; Flow = flow; Start = start;
        DispatchId = dispatch; Target = target; Related = related; NameId = name; Kind = kind; Route = route; HandledBefore = handled; HandledEventsToo = too;
    }
}
