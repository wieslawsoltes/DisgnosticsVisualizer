using System.Diagnostics;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal readonly record struct StylingContext(CaptureSession? Session, long Id);

public static partial class Probe
{
    [ThreadStatic] private static StylingContext _stylingContext;
    [ThreadStatic] private static int _stylingDepth;

    /// <summary>Observes identities only. No selector formatting, replay, subscription or activator evaluation.</summary>
    public static StyleEvaluationScope BeginStyleEvaluation(string site, int kind, object? subject,
        object? target, object? context, bool input)
    {
        var previous = _stylingContext; _stylingContext = default;
        var scope = new StyleEvaluationScope(previous, ++_stylingDepth, Environment.CurrentManagedThreadId);
        if (kind is < 1 or > 5) return scope;
        var operation = (StyleEvaluationKind)kind; var category = StyleEvaluationEvent.Category(operation);
        var session = Current;
        if (session?.Options.TrackStyleEvaluations != true)
        { if (operation <= StyleEvaluationKind.Attach) scope.Fallback = Enter((int)category, site, subject, target); return scope; }
        if (_insideProbe || _suppressionDepth != 0 || (session.Options.SubsystemMask & (1UL << (int)category)) == 0 ||
            (_stylingDepth > 1 ? !ReferenceEquals(previous.Session, session) : !session.Recording)) return scope;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return scope; }
            if (_stylingDepth == 1 && ++writer.Samples % session.Options.SampleEvery != 0) return scope;
            var name = session.Intern(writer, site); var subjectId = session.GetObjectId(subject);
            var targetId = session.GetObjectId(target); var contextId = session.GetObjectId(context);
            var id = writer.NextId(); var parent = LogicalParent(session, writer); var restore = writer.Parent;
            var flow = session.Options.FlowExecutionContext ? Ambient.Value : default;
            if (session.Options.FlowExecutionContext) Ambient.Value = new(session.Id, id);
            writer.Parent = id; _stylingContext = new(session, id);
            scope.Initialize(session, writer, id, parent, subjectId, name, operation, restore, flow);
            if (!writer.TryWrite(new(writer.NextId(), id, scope.Start, 0, subjectId, targetId, writer.ThreadId, name,
                category, TraceEventKind.Link, StyleEvaluationEvent.BeginCode(operation, input), previous.Id))) session.Drop();
            if (!writer.TryWrite(new(writer.NextId(), id, scope.Start, 0, contextId, 0, writer.ThreadId, name,
                category, TraceEventKind.Link, StyleEvaluationEvent.ContextCode(operation), previous.Id))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
        return scope;
    }
    internal static void StyleReturned(ref StyleEvaluationScope scope, int outcome, object? activator, bool changed)
    {
        if (scope.Session is not { } session || _insideProbe) return;
        _insideProbe = true;
        try
        {
            if (outcome is < 1 or > 8) { session.Fault(); return; }
            scope.Outcome = (StyleEvaluationOutcome)outcome; scope.Changed = changed;
            scope.ActivatorId = session.GetObjectId(activator);
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    internal static void EndStyle(ref StyleEvaluationScope scope)
    {
        if (scope.Depth == 0) return;
        if (scope.Thread != Environment.CurrentManagedThreadId || _stylingDepth != scope.Depth) { Current?.Fault(); return; }
        _stylingDepth--; _stylingContext = scope.Previous; scope.Depth = 0;
        if (scope.Session is not { } session || scope.Writer is not { } writer) { scope.Fallback.Dispose(); return; }
        if (_insideProbe || writer.Parent != scope.Id) { session.Fault(); return; }
        _insideProbe = true;
        try
        {
            if (session.Options.FlowExecutionContext && Ambient.Value.SessionId == session.Id) Ambient.Value = scope.PreviousFlow;
            writer.Parent = scope.Restore;
            if (ReferenceEquals(Current, session) && !writer.TryWrite(new(scope.Id, scope.Parent, scope.Start,
                Math.Max(0, Stopwatch.GetTimestamp() - scope.Start), scope.SubjectId, scope.ActivatorId, writer.ThreadId, scope.NameId,
                StyleEvaluationEvent.Category(scope.Operation), TraceEventKind.Span,
                StyleEvaluationEvent.EndCode(scope.Operation, scope.Outcome, scope.Changed), scope.Previous.Id))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
}

public ref struct StyleEvaluationScope
{
    internal readonly StylingContext Previous;
    internal int Depth;
    internal readonly int Thread;
    internal TraceScope Fallback;
    internal CaptureSession? Session;
    internal CaptureWriter? Writer;
    internal long Id, Parent, Start, SubjectId, ActivatorId, Restore;
    internal int NameId;
    internal StyleEvaluationKind Operation;
    internal StyleEvaluationOutcome Outcome;
    internal bool Changed;
    internal TraceContext PreviousFlow;
    internal StyleEvaluationScope(StylingContext previous, int depth, int thread)
    { this = default; Previous = previous; Depth = depth; Thread = thread; }
    internal void Initialize(CaptureSession session, CaptureWriter writer, long id, long parent, long subject,
        int name, StyleEvaluationKind operation, long restore, TraceContext flow)
    { Session = session; Writer = writer; Id = id; Parent = parent; SubjectId = subject; NameId = name; Operation = operation; Restore = restore; PreviousFlow = flow; Start = Stopwatch.GetTimestamp(); }
    public void Returned(int outcome, object? activator, bool changed) => Probe.StyleReturned(ref this, outcome, activator, changed);
    public void Dispose() => Probe.EndStyle(ref this);
}
