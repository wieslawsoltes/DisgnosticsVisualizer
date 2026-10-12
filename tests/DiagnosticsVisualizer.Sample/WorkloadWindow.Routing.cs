using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DiagnosticsVisualizer.Sample;

public sealed partial class WorkloadWindow
{
    private static readonly RoutedEvent<RoutedEventArgs> InvestigationEvent =
        RoutedEvent.Register<WorkloadWindow, RoutedEventArgs>("DiagnosticsRouting", RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    private static readonly RoutedEvent<RoutedEventArgs> FailureEvent =
        RoutedEvent.Register<WorkloadWindow, RoutedEventArgs>("DiagnosticsRoutingFailure", RoutingStrategies.Direct);

    private void ExerciseRouting(Button button)
    {
        var log = new List<string>();
        EventHandler<RoutedEventArgs> preview = (_, e) => { log.Add("root-tunnel"); e.Handled = true; };
        EventHandler<RoutedEventArgs> blocked = (_, _) => log.Add("SHOULD-NOT-RUN");
        EventHandler<RoutedEventArgs> too = (_, e) => { log.Add("leaf-too"); e.Handled = false; };
        EventHandler<RoutedEventArgs> leaf = (_, _) => { log.Add("leaf-bubble"); _model.Title = "Routed mutation"; };
        EventHandler<RoutedEventArgs> root = (_, _) => log.Add("root-bubble");
        EventHandler<RoutedEventArgs> fail = (_, _) => throw new InvalidOperationException("Expected routed handler failure");
        AddHandler(InvestigationEvent, preview, RoutingStrategies.Tunnel);
        AddHandler(InvestigationEvent, root, RoutingStrategies.Bubble);
        button.AddHandler(InvestigationEvent, leaf, RoutingStrategies.Bubble);
        button.AddHandler(InvestigationEvent, too, RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(InvestigationEvent, blocked, RoutingStrategies.Tunnel);
        button.AddHandler(FailureEvent, fail, RoutingStrategies.Direct);
        try
        {
            var args = new RoutedEventArgs(InvestigationEvent); button.RaiseEvent(args);
            if (args.Handled || string.Join(",", log) != "root-tunnel,leaf-too,leaf-bubble,root-bubble")
                throw new InvalidOperationException("Routed instrumentation changed propagation order or handled semantics.");
            try { button.RaiseEvent(new RoutedEventArgs(FailureEvent)); throw new Exception("Routed exception lost"); }
            catch (InvalidOperationException error) when (error.Message == "Expected routed handler failure") { }
            Console.WriteLine("ROUTING_WORKLOAD_OK handlers=4 handled=false fault=observed");
        }
        finally
        {
            RemoveHandler(InvestigationEvent, preview); RemoveHandler(InvestigationEvent, root);
            button.RemoveHandler(InvestigationEvent, leaf); button.RemoveHandler(InvestigationEvent, too);
            button.RemoveHandler(InvestigationEvent, blocked); button.RemoveHandler(FailureEvent, fail);
        }
    }
}
