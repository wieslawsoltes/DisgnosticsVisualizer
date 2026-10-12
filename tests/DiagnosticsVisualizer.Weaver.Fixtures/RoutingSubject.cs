using Avalonia.Interactivity;

namespace DiagnosticsVisualizer.Weaver.Fixtures;

// Uses the actual framework route loop; no diagnostics API calls in this workload.
public static class RoutingSubject
{
    private sealed class Node : Interactive { }
    public static string Exercise()
    {
        var ev = RoutedEvent.Register<Node, RoutedEventArgs>("RoutingProbe", RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        var direct = RoutedEvent.Register<Node, RoutedEventArgs>("NestedProbe", RoutingStrategies.Direct);
        var source = new Node(); var parent = new Node(); var audit = new List<string>();
        EventHandler<RoutedEventArgs> rootTunnel = (_, e) => { audit.Add("root-tunnel"); e.Handled = true; };
        EventHandler<RoutedEventArgs> suppressed = (_, _) => audit.Add("SHOULD-NOT-RUN");
        EventHandler<RoutedEventArgs> too = (_, e) => { audit.Add("leaf-too"); e.Handled = false; };
        EventHandler<RoutedEventArgs> bubble = (_, _) =>
        {
            audit.Add("leaf-bubble");
            using var nested = new EventRoute(direct);
            nested.Add(source, new EventHandler<RoutedEventArgs>((_, _) => audit.Add("nested")), RoutingStrategies.Direct);
            nested.RaiseEvent(source, new(direct));
        };
        EventHandler<RoutedEventArgs> rootBubble = (_, _) => audit.Add("root-bubble");
        using (var route = new EventRoute(ev))
        {
            route.Add(source, bubble, RoutingStrategies.Bubble, adapter: Adapt);
            route.Add(source, too, RoutingStrategies.Tunnel, handledEventsToo: true, adapter: Adapt);
            route.Add(source, suppressed, RoutingStrategies.Tunnel, adapter: Adapt);
            route.Add(parent, rootBubble, RoutingStrategies.Bubble, adapter: Adapt);
            route.Add(parent, rootTunnel, RoutingStrategies.Tunnel, adapter: Adapt);
            route.RaiseEvent(source, new(ev));
        }
        foreach (var useAdapter in new[] { true, false })
        {
            using var fault = new EventRoute(direct);
            EventHandler<RoutedEventArgs> fail = (_, _) => throw new InvalidOperationException("original failure");
            fault.Add(source, fail, RoutingStrategies.Direct, adapter: useAdapter ? Adapt : null);
            try { fault.RaiseEvent(source, new(direct)); throw new Exception("Missing failure"); }
            catch (InvalidOperationException error) when (useAdapter && error.Message == "original failure") { audit.Add("typed-fault"); }
            catch (System.Reflection.TargetInvocationException error) when (!useAdapter && error.InnerException?.Message == "original failure") { audit.Add("dynamic-fault"); }
        }
        using var nulls = new EventRoute(direct);
        try { nulls.RaiseEvent(null!, new(direct)); } catch (ArgumentNullException e) when (e.ParamName == "source") { audit.Add("null-source"); }
        try { nulls.RaiseEvent(source, null!); } catch (ArgumentNullException e) when (e.ParamName == "e") { audit.Add("null-args"); }
        return string.Join(",", audit);
    }
    private static void Adapt(Delegate handler, object sender, RoutedEventArgs args) => ((EventHandler<RoutedEventArgs>)handler)(sender, args);
}
