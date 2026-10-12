using System.Diagnostics;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal readonly record struct BindingCaptureContext(CaptureSession? Session, long Id);

public static partial class Probe
{
    [ThreadStatic] private static BindingCaptureContext _bindingContext;
    [ThreadStatic] private static int _bindingDepth;

    // This guard deliberately does not test Recording: admitted nested operations may finish after pause.
    public static bool BindingOperationsEnabled => Current?.Options.TrackBindingOperations == true && !_insideProbe && _suppressionDepth == 0;

    /// <summary>Classifies existing references only; never formats a value or invokes application equality.</summary>
    public static int BindingFacts(object? value, bool hasValue, object? unset, object? unchanged, object? doNothing,
        int running, int error, int valueChanged, int errorChanged, int produceValue)
    {
        var kind = !hasValue ? BindingValueKind.Unknown : value is null ? BindingValueKind.Null :
            ReferenceEquals(value, unset) ? BindingValueKind.Unset : ReferenceEquals(value, unchanged) ? BindingValueKind.Unchanged :
            ReferenceEquals(value, doNothing) ? BindingValueKind.DoNothing : BindingValueKind.Value;
        static bool? Flag(int flag) => flag switch { 0 => false, 1 => true, _ => null };
        return BindingOperationEvent.Facts(kind, Flag(running), error is >= 0 and <= 2 ? error : null,
            Flag(valueChanged), Flag(errorChanged), Flag(produceValue));
    }

    /// <summary>Observes invocation, not successful attachment, delivery or property precedence.</summary>
    public static BindingOperationScope BeginBindingOperation(string site, int kind, object? expression,
        object? target, object? property, object? participant, object? input, int facts, long detail)
    {
        var previous = _bindingContext; _bindingContext = default;
        var scope = new BindingOperationScope(previous, ++_bindingDepth, Environment.CurrentManagedThreadId);
        if (kind is < 1 or > 21 || !BindingOperationEvent.ValidFacts(facts) ||
            detail != long.MinValue && (detail < int.MinValue || detail > int.MaxValue)) return scope;
        var session = Current;
        if (session?.Options.TrackBindingOperations != true)
        { scope.Fallback = Enter((int)Subsystem.Bindings, site, expression, target); return scope; }
        if (_insideProbe || _suppressionDepth != 0 || (session.Options.SubsystemMask & (1UL << (int)Subsystem.Bindings)) == 0 ||
            (_bindingDepth > 1 ? !ReferenceEquals(previous.Session, session) : !session.Recording)) return scope;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return scope; }
            if (_bindingDepth == 1 && ++writer.Samples % session.Options.SampleEvery != 0) return scope;
            var name = session.Intern(writer, site); var expressionId = session.GetObjectId(expression);
            var targetId = session.GetObjectId(target); var propertyId = session.GetObjectId(property);
            var participantId = session.GetObjectId(participant);
            var inputId = BindingOperationEvent.ValueKind(facts) == BindingValueKind.Value ? session.GetObjectId(input) : 0;
            var id = writer.NextId(); var parent = LogicalParent(session, writer); var restore = writer.Parent;
            var flow = session.Options.FlowExecutionContext ? Ambient.Value : default;
            if (session.Options.FlowExecutionContext) Ambient.Value = new(session.Id, id);
            writer.Parent = id; _bindingContext = new(session, id);
            scope.Initialize(session, writer, id, parent, expressionId, name, (BindingOperationKind)kind, restore, flow);
            Write(new(writer.NextId(), id, scope.Start, 0, expressionId, targetId, writer.ThreadId, name,
                Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.BeginCode(scope.Operation, facts), previous.Id));
            Write(new(writer.NextId(), id, scope.Start, 0, propertyId, participantId, writer.ThreadId, name,
                Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.ContextCode(scope.Operation), previous.Id));
            Write(new(writer.NextId(), id, scope.Start, 0, inputId, 0, writer.ThreadId, name,
                Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.InputCode(scope.Operation), detail));
            void Write(TraceEvent e) { if (!writer.TryWrite(e)) session.Drop(); }
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
        return scope;
    }
    internal static void BindingReturned(ref BindingOperationScope scope, int outcome, object? value, int facts)
    {
        if (scope.Session is not { } session || _insideProbe || !ReferenceEquals(Current, session)) return;
        _insideProbe = true;
        try
        {
            if (outcome is < 1 or > 3 || !BindingOperationEvent.ValidFacts(facts) ||
                outcome > 1 && scope.Operation != BindingOperationKind.WriteSource) { session.Fault(); return; }
            scope.Outcome = (BindingOperationOutcome)outcome; scope.OutputFacts = facts;
            scope.OutputId = BindingOperationEvent.ValueKind(facts) == BindingValueKind.Value ? session.GetObjectId(value) : 0;
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    internal static void EndBinding(ref BindingOperationScope scope)
    {
        if (scope.Depth == 0) return;
        if (scope.Thread != Environment.CurrentManagedThreadId || _bindingDepth != scope.Depth) { Current?.Fault(); return; }
        _bindingDepth--; _bindingContext = scope.Previous; scope.Depth = 0;
        if (scope.Session is not { } session || scope.Writer is not { } writer) { scope.Fallback.Dispose(); return; }
        if (_insideProbe || writer.Parent != scope.Id) { session.Fault(); return; }
        _insideProbe = true;
        try
        {
            if (session.Options.FlowExecutionContext && Ambient.Value.SessionId == session.Id) Ambient.Value = scope.PreviousFlow;
            writer.Parent = scope.Restore;
            if (ReferenceEquals(Current, session) && !writer.TryWrite(new(scope.Id, scope.Parent, scope.Start,
                Math.Max(0, Stopwatch.GetTimestamp() - scope.Start), scope.ExpressionId, scope.OutputId, writer.ThreadId, scope.NameId,
                Subsystem.Bindings, TraceEventKind.Span, BindingOperationEvent.EndCode(scope.Operation, scope.Outcome, scope.OutputFacts), scope.Previous.Id))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
}

public ref struct BindingOperationScope
{
    internal readonly BindingCaptureContext Previous;
    internal int Depth;
    internal readonly int Thread;
    internal TraceScope Fallback;
    internal CaptureSession? Session;
    internal CaptureWriter? Writer;
    internal long Id, Parent, Start, ExpressionId, OutputId, Restore;
    internal int NameId, OutputFacts;
    internal BindingOperationKind Operation;
    internal BindingOperationOutcome Outcome;
    internal TraceContext PreviousFlow;
    public bool IsRecorded => Session is not null && ReferenceEquals(Probe.Current, Session);
    internal BindingOperationScope(BindingCaptureContext previous, int depth, int thread)
    { this = default; Previous = previous; Depth = depth; Thread = thread; }
    internal void Initialize(CaptureSession session, CaptureWriter writer, long id, long parent, long expression,
        int name, BindingOperationKind operation, long restore, TraceContext flow)
    { Session = session; Writer = writer; Id = id; Parent = parent; ExpressionId = expression; NameId = name; Operation = operation; Restore = restore; PreviousFlow = flow; Start = Stopwatch.GetTimestamp(); }
    public void Returned(int outcome, object? value, int facts) => Probe.BindingReturned(ref this, outcome, value, facts);
    public void Dispose() => Probe.EndBinding(ref this);
}
