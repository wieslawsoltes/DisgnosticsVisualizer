using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public static partial class DemoTraceFactory
{
    private static void AddBindingExample(List<TraceEvent> events, ref long lastId, ref Symbol[] symbols, ref ObjectInfo[] objects)
    {
        var id = lastId;
        var nextName = symbols.Max(s => s.Id);
        var names = new List<Symbol>();
        objects = [..objects, new(150, "SyntheticBindingExpression", "Demonstration binding"),
            new(151, "AvaloniaProperty", "Demonstration property"), new(152, "SyntheticSink", "Demonstration sink"),
            new(153, "SyntheticValue", "Demonstration value (no payload)"), new(154, "SyntheticDynamicResourceExpression", "Demonstration resource expression")];
        long Call(BindingOperationKind kind, long start, long duration, long expression, long parent = 0,
            long target = 0, bool running = true, BindingValueKind input = BindingValueKind.Unknown,
            BindingValueKind output = BindingValueKind.Unknown, bool? produce = null, int? error = null, int thread = 1)
        {
            var operation = ++id; var name = ++nextName;
            names.Add(new(name, "Synthetic binding example: " + kind));
            var participant = expression == 154 ? 111 : 152;
            var valueChanged = kind == BindingOperationKind.Deliver ? (bool?)true : null;
            var facts = BindingOperationEvent.Facts(input, running, error, valueChanged, null, produce);
            events.Add(new(++id, operation, start, 0, expression, target, thread, name, Subsystem.Bindings,
                TraceEventKind.Link, BindingOperationEvent.BeginCode(kind, facts), parent));
            events.Add(new(++id, operation, start, 0, 151, participant, thread, name, Subsystem.Bindings,
                TraceEventKind.Link, BindingOperationEvent.ContextCode(kind), parent));
            events.Add(new(++id, operation, start, 0, input == BindingValueKind.Value ? 153 : 0, 0, thread, name, Subsystem.Bindings,
                TraceEventKind.Link, BindingOperationEvent.InputCode(kind), long.MinValue));
            var after = BindingOperationEvent.Facts(output, kind is not (BindingOperationKind.Stop or BindingOperationKind.Dispose), error, valueChanged, null, produce);
            events.Add(new(operation, parent, start, duration, expression, output == BindingValueKind.Value ? 153 : 0, thread, name,
                Subsystem.Bindings, TraceEventKind.Span, BindingOperationEvent.EndCode(kind, BindingOperationOutcome.Returned, after), parent));
            return operation;
        }
        Call(BindingOperationKind.Attach, 500, 80, 150, target: 5, running: false);
        var start = Call(BindingOperationKind.Start, 600, 450, 150, running: false, produce: true);
        var publish = Call(BindingOperationKind.Publish, 670, 150, 150, start, input: BindingValueKind.Value, output: BindingValueKind.Value);
        Call(BindingOperationKind.Deliver, 700, 30, 150, publish, input: BindingValueKind.Value, output: BindingValueKind.Value);
        // Deliberately separate asynchronous publication and delivery roots: equal value
        // identity is not an automatic causal edge or a fabricated delivery latency.
        Call(BindingOperationKind.Publish, 1600, 40, 150, input: BindingValueKind.Null, output: BindingValueKind.Null, thread: 8);
        Call(BindingOperationKind.Deliver, 1900, 30, 150, input: BindingValueKind.Null, output: BindingValueKind.Null);
        Call(BindingOperationKind.Attach, 2100, 60, 154, target: 4, running: false);
        var invalidation = Call(BindingOperationKind.ResourceInvalidated, 2300, 250, 154);
        var resolve = Call(BindingOperationKind.ResolveResource, 2320, 190, 154, invalidation);
        Call(BindingOperationKind.Publish, 2400, 40, 154, resolve, input: BindingValueKind.Value, output: BindingValueKind.Value);
        Call(BindingOperationKind.Stop, 2700, 40, 154);
        Call(BindingOperationKind.Dispose, 2800, 40, 154, running: false);
        symbols = [..symbols, ..names]; lastId = id;
    }
}
