using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

internal static class BindingCaptureChecks
{
    public static object Verify(TraceView trace)
    {
        var index = new BindingOperationIndex(trace);
        foreach (var kind in new[] { BindingOperationKind.Attach, BindingOperationKind.Start, BindingOperationKind.Stop, BindingOperationKind.Dispose,
            BindingOperationKind.Publish, BindingOperationKind.Deliver, BindingOperationKind.Complete, BindingOperationKind.Convert,
            BindingOperationKind.ConvertBack, BindingOperationKind.WriteSource, BindingOperationKind.NodeValue, BindingOperationKind.NodeError,
            BindingOperationKind.ResourceSubscribe, BindingOperationKind.ResourceUnsubscribe, BindingOperationKind.ResourceInvalidated,
            BindingOperationKind.ResolveResource, BindingOperationKind.ThemeChanged })
            if (!index.Calls.Any(c => c.IsComplete && c.Kind == kind)) throw new InvalidDataException($"Missing completed binding observation: {kind}.");
        if (index.InvalidMarkers != 0 || !index.Calls.Any(c => c.OutputKind == "DoNothing") ||
            !index.Calls.Any(c => c.OutputKind == "Unset" && c.Error == "Binding error") ||
            !index.Calls.Any(c => c.InputKind == "Null" && c.Kind == BindingOperationKind.Publish))
            throw new InvalidDataException("Binding sentinels, converter errors or marker consistency failed validation.");
        var target = trace.Checkpoints.SelectMany(c => c.Data.Nodes).FirstOrDefault(n => n.Name == "BindingDispatchProbe")?.Id ?? 0;
        if (target == 0) throw new InvalidDataException("Binding dispatch target checkpoint was not retained.");
        var expressions = index.Calls.Where(c => c.Kind == BindingOperationKind.Attach && c.TargetId == target && c.Outcome == BindingOperationOutcome.Returned).Select(c => c.ExpressionId).ToHashSet();
        var publications = index.Calls.Where(c => c.Kind == BindingOperationKind.Publish && c.IsComplete && expressions.Contains(c.ExpressionId) && c.InputId > 0).ToArray();
        // The reference workload publishes a unique object once. Do not turn this identity check
        // into a general assertion that repeated values establish causal delivery pairing.
        var crossThread = index.Calls.Count(delivery => delivery.Kind == BindingOperationKind.Deliver && delivery.IsComplete &&
            publications.Any(p => p.ExpressionId == delivery.ExpressionId && p.InputId == delivery.InputId &&
                p.End!.Value.ThreadId != delivery.End!.Value.ThreadId && p.Timestamp <= delivery.Timestamp));
        if (crossThread == 0) throw new InvalidDataException("The original background publication/UI sink-delivery boundary was not observed.");
        if (!index.Calls.Any(c => c.Kind == BindingOperationKind.ResourceInvalidated && index.Descendants(c.Id).Any(d => d.Kind == BindingOperationKind.ResolveResource)))
            throw new InvalidDataException("No observed dynamic-resource invalidation-to-resolution chain.");
        return new
        {
            Operations = index.Calls.Length, Roots = index.Roots.Length, index.InvalidMarkers,
            Expressions = index.Calls.Select(c => c.ExpressionId).Where(id => id != 0).Distinct().Count(),
            Publications = index.Calls.Count(c => c.Kind == BindingOperationKind.Publish), Deliveries = index.Calls.Count(c => c.Kind == BindingOperationKind.Deliver),
            ConverterErrors = index.Calls.Count(c => c.Kind == BindingOperationKind.Convert && c.Error == "Binding error"),
            Invalidations = index.Calls.Count(c => c.Kind == BindingOperationKind.ResourceInvalidated),
            ThemeChanges = index.Calls.Count(c => c.Kind == BindingOperationKind.ThemeChanged),
            SourceWrites = index.Calls.Count(c => c.Kind == BindingOperationKind.WriteSource), CrossThreadDeliveries = crossThread,
            Kinds = index.Calls.GroupBy(c => c.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count())
        };
    }
}
