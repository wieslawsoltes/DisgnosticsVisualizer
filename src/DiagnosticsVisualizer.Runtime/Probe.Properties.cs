using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal readonly record struct PropertyCaptureContext(CaptureSession? Session, long Id);
public static partial class Probe
{
    [ThreadStatic] private static PropertyCaptureContext _propertyContext;
    [ThreadStatic] private static int _propertyDepth;
    public static bool PropertyOperationsEnabled => Current?.Options.TrackPropertyOperations == true && !_insideProbe && _suppressionDepth == 0;

    public static PropertyOperationScope BeginPropertyOperation(string site, int operation, object? target, object? property,
        string? propertyName, object? participant, long priority, int flags)
    {
        var previous = _propertyContext; _propertyContext = default;
        var scope = new PropertyOperationScope(previous, ++_propertyDepth, Environment.CurrentManagedThreadId);
        if (operation is < 1 or > 8 || !PropertyOperationEvent.ValidFlags((PropertyOperationKind)operation, flags) ||
            (priority != long.MinValue && (priority < int.MinValue || priority > int.MaxValue))) return scope;
        var session = Current;
        if (session?.Options.TrackPropertyOperations != true) { scope.Fallback = Enter((int)Subsystem.Properties, site, target, property); return scope; }
        if (_insideProbe || _suppressionDepth != 0 || (session.Options.SubsystemMask & (1UL << (int)Subsystem.Properties)) == 0 ||
            (_propertyDepth > 1 ? !ReferenceEquals(previous.Session, session) : !session.Recording)) return scope;
        _insideProbe = true;
        try
        {
            var writer = session.Writer; if (writer is null) { session.Drop(); return scope; }
            if (_propertyDepth == 1 && ++writer.Samples % session.Options.SampleEvery != 0) return scope;
            var name = session.Intern(writer, site); var targetId = session.GetObjectId(target); var propertyId = session.GetObjectId(property);
            session.LabelProperty(propertyId, propertyName);
            var participantId = session.GetObjectId(participant); var id = writer.NextId(); var parent = LogicalParent(session, writer);
            var restore = writer.Parent; var flow = session.Options.FlowExecutionContext ? Ambient.Value : default;
            if (session.Options.FlowExecutionContext) Ambient.Value = new(session.Id, id);
            writer.Parent = id; _propertyContext = new(session, id);
            scope.Initialize(session, writer, id, parent, targetId, propertyId, name, (PropertyOperationKind)operation, flags, restore, flow);
            if (!writer.TryWrite(new(writer.NextId(), id, scope.Start, 0, targetId, propertyId, writer.ThreadId, name,
                Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(scope.Operation, 1, flags), previous.Id))) session.Drop();
            if (!writer.TryWrite(new(writer.NextId(), id, scope.Start, 0, participantId, 0, writer.ThreadId, name,
                Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(scope.Operation, 2), priority))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
        return scope;
    }
    internal static void PropertyValue<T>(ref PropertyOperationScope scope, int phase, T value, bool available)
    {
        if (scope.Session is not { } session || _insideProbe || !ReferenceEquals(Current, session) || phase is < 3 or > 4) return;
        _insideProbe = true;
        try
        {
            if ((scope.Values & (1 << phase)) != 0) { session.Fault(); return; }
            scope.Values |= 1 << phase;
            var atom = available ? CaptureValue(session, value) : default;
            var writer = scope.Writer!;
            if (!writer.TryWrite(new(writer.NextId(), scope.Id, scope.Start, 0, atom.ObjectId, 0, writer.ThreadId, scope.NameId,
                Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(scope.Operation, phase, value: atom.Kind), atom.Bits))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    private static PropertyValue CaptureValue<T>(CaptureSession session, T value)
    {
        // JIT folds typeof(T) branches; reference types never invoke user formatters/equality.
        if (!typeof(T).IsValueType)
        {
            object? reference = value;
            if (reference is null) return new(PropertyValueKind.Null);
            if (reference is string) return new(PropertyValueKind.Redacted);
            if (!reference.GetType().IsValueType) return new(PropertyValueKind.Reference, ObjectId: session.GetObjectId(reference));
            if (!session.Options.CapturePropertyScalars) return new(PropertyValueKind.Redacted);
            // Values passed through object APIs are already boxed; inspect only known primitives, never re-box.
            return reference switch
            {
                bool v => new(PropertyValueKind.Boolean, v ? 1 : 0), int v => new(PropertyValueKind.Signed, v), long v => new(PropertyValueKind.Signed, v),
                short v => new(PropertyValueKind.Signed, v), sbyte v => new(PropertyValueKind.Signed, v),
                uint v => new(PropertyValueKind.Unsigned, v), ulong v => new(PropertyValueKind.Unsigned, unchecked((long)v)),
                ushort v => new(PropertyValueKind.Unsigned, v), byte v => new(PropertyValueKind.Unsigned, v),
                double v => new(PropertyValueKind.Float64, BitConverter.DoubleToInt64Bits(v)), float v => new(PropertyValueKind.Float32, BitConverter.SingleToInt32Bits(v)),
                _ => new(PropertyValueKind.Redacted)
            };
        }
        if (!session.Options.CapturePropertyScalars) return new(PropertyValueKind.Redacted);
        if (typeof(T) == typeof(bool)) return new(PropertyValueKind.Boolean, Unsafe.As<T, bool>(ref value) ? 1 : 0);
        if (typeof(T) == typeof(double)) return new(PropertyValueKind.Float64, BitConverter.DoubleToInt64Bits(Unsafe.As<T, double>(ref value)));
        if (typeof(T) == typeof(float)) return new(PropertyValueKind.Float32, BitConverter.SingleToInt32Bits(Unsafe.As<T, float>(ref value)));
        if (typeof(T) == typeof(int)) return new(PropertyValueKind.Signed, Unsafe.As<T, int>(ref value));
        if (typeof(T) == typeof(long)) return new(PropertyValueKind.Signed, Unsafe.As<T, long>(ref value));
        if (typeof(T) == typeof(short)) return new(PropertyValueKind.Signed, Unsafe.As<T, short>(ref value));
        if (typeof(T) == typeof(sbyte)) return new(PropertyValueKind.Signed, Unsafe.As<T, sbyte>(ref value));
        if (typeof(T) == typeof(uint)) return new(PropertyValueKind.Unsigned, Unsafe.As<T, uint>(ref value));
        if (typeof(T) == typeof(ulong)) return new(PropertyValueKind.Unsigned, unchecked((long)Unsafe.As<T, ulong>(ref value)));
        if (typeof(T) == typeof(ushort)) return new(PropertyValueKind.Unsigned, Unsafe.As<T, ushort>(ref value));
        if (typeof(T) == typeof(byte)) return new(PropertyValueKind.Unsigned, Unsafe.As<T, byte>(ref value));
        return new(PropertyValueKind.Redacted); // Structs, enums, pointers, nullable values and chars stay redacted.
    }
    internal static void EndProperty(ref PropertyOperationScope scope)
    {
        if (scope.Depth == 0) return;
        if (scope.Thread != Environment.CurrentManagedThreadId || _propertyDepth != scope.Depth) { Current?.Fault(); return; }
        _propertyDepth--; _propertyContext = scope.Previous; scope.Depth = 0;
        if (scope.Session is not { } session || scope.Writer is not { } writer) { scope.Fallback.Dispose(); return; }
        if (_insideProbe || writer.Parent != scope.Id) { session.Fault(); return; }
        // Emit explicit unknown values for absent inputs/outputs, including failed constructors/setters.
        if ((scope.Values & (1 << 3)) == 0) PropertyValue<object?>(ref scope, 3, null, false);
        if ((scope.Values & (1 << 4)) == 0) PropertyValue<object?>(ref scope, 4, null, false);
        _insideProbe = true;
        try
        {
            if (session.Options.FlowExecutionContext && Ambient.Value.SessionId == session.Id) Ambient.Value = scope.PreviousFlow;
            writer.Parent = scope.Restore;
            if (ReferenceEquals(Current, session) && !writer.TryWrite(new(scope.Id, scope.Parent, scope.Start,
                Math.Max(0, Stopwatch.GetTimestamp() - scope.Start), scope.TargetId, scope.PropertyId, writer.ThreadId, scope.NameId,
                Subsystem.Properties, TraceEventKind.Span, PropertyOperationEvent.Code(scope.Operation, 5, scope.Flags, outcome: scope.Outcome), scope.Previous.Id))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
}
public ref struct PropertyOperationScope
{
    internal readonly PropertyCaptureContext Previous;
    internal readonly int Thread;
    internal int Depth, Values, Flags, NameId;
    internal TraceScope Fallback;
    internal CaptureSession? Session;
    internal CaptureWriter? Writer;
    internal long Id, Parent, Start, TargetId, PropertyId, Restore;
    internal PropertyOperationKind Operation;
    internal PropertyOperationOutcome Outcome;
    internal TraceContext PreviousFlow;
    public bool IsRecorded => Session is not null && ReferenceEquals(Probe.Current, Session);
    internal PropertyOperationScope(PropertyCaptureContext previous, int depth, int thread) { this = default; Previous = previous; Depth = depth; Thread = thread; }
    internal void Initialize(CaptureSession session, CaptureWriter writer, long id, long parent, long target, long property, int name,
        PropertyOperationKind operation, int flags, long restore, TraceContext flow)
    { Session = session; Writer = writer; Id = id; Parent = parent; TargetId = target; PropertyId = property; NameId = name; Operation = operation; Flags = flags; Restore = restore; PreviousFlow = flow; Start = Stopwatch.GetTimestamp(); }
    public void Value<T>(int phase, T value, bool available = true) => Probe.PropertyValue(ref this, phase, value, available);
    public void ObserveValue<T>(T value, bool oldValue) => Value(oldValue ? 3 : 4, value);
    public void Returned() => Outcome = PropertyOperationOutcome.Returned;
    public void DirectResult(bool changed) => Outcome = changed ? PropertyOperationOutcome.Changed : PropertyOperationOutcome.Unchanged;
    public void Dispose() => Probe.EndProperty(ref this);
}
