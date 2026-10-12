using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public static partial class DemoTraceFactory
{
    private static void AddPropertyExample(List<TraceEvent> events, ref long lastId, ref Symbol[] symbols, ref ObjectInfo[] objects)
    {
        var id = lastId; var nameId = symbols.Max(s => s.Id); var names = new List<Symbol>();
        objects = [..objects, new(160, "AvaloniaProperty", "Synthetic opacity"), new(161, "SyntheticValueEntry", "Demonstration style entry")];
        void Call(long time, PropertyOperationKind kind, double before, double after, long priority, bool effective, bool baseValue = false)
        {
            var operation = ++id; var name = ++nameId; var flags = effective ? 8 : baseValue ? 16 : 0;
            names.Add(new(name, "Synthetic property example: " + kind));
            events.Add(new(++id, operation, time, 0, 5, 160, 1, name, Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(kind, 1, flags)));
            events.Add(new(++id, operation, time, 0, kind == PropertyOperationKind.ValueEntry ? 161 : 0, 0, 1, name, Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(kind, 2), priority));
            events.Add(new(++id, operation, time, 0, 0, 0, 1, name, Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(kind, 3, value: kind == PropertyOperationKind.Notify ? PropertyValueKind.Float64 : PropertyValueKind.Unknown), kind == PropertyOperationKind.Notify ? BitConverter.DoubleToInt64Bits(before) : 0));
            events.Add(new(++id, operation, time, 0, 0, 0, 1, name, Subsystem.Properties, TraceEventKind.Link, PropertyOperationEvent.Code(kind, 4, value: kind == PropertyOperationKind.Clear ? PropertyValueKind.Unknown : PropertyValueKind.Float64), kind == PropertyOperationKind.Clear ? 0 : BitConverter.DoubleToInt64Bits(after)));
            events.Add(new(operation, 0, time, 20, 5, 160, 1, name, Subsystem.Properties, TraceEventKind.Span, PropertyOperationEvent.Code(kind, 5, flags, outcome: PropertyOperationOutcome.Returned)));
        }
        Call(100, PropertyOperationKind.ValueEntry, 1, .25, 1, false);
        Call(200, PropertyOperationKind.Notify, 1, .25, 1, true);
        Call(500, PropertyOperationKind.Set, .25, .8, 0, false);
        Call(600, PropertyOperationKind.Notify, .25, .8, 0, true);
        Call(900, PropertyOperationKind.Notify, .25, .4, 1, false, true);
        Call(1200, PropertyOperationKind.Clear, .8, 0, 0, false);
        Call(1300, PropertyOperationKind.Notify, .8, .4, 1, true);
        Call(1700, PropertyOperationKind.Notify, .4, 1, int.MaxValue, true);
        symbols = [..symbols, ..names]; lastId = id;
    }
}
