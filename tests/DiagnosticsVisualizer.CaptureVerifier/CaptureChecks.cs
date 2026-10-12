using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

internal static class CaptureChecks
{
    // Verify observed causality, not merely the presence of an async method name.
    public static int CountAsyncNotifications(TraceView view)
    {
        var events = view.Events.ToDictionary(e => e.Id);
        var count = 0;
        foreach (var notification in view.Events.Where(e => e.Subsystem == Subsystem.PropertyNotifications))
        {
            var current = notification;
            var seen = new HashSet<long>();
            while (current.ParentId != 0 && seen.Add(current.Id) && events.TryGetValue(current.ParentId, out current))
            {
                if (current.ThreadId != notification.ThreadId &&
                    view.SourceSites.TryGetValue(current.NameId, out var site) && site.AsyncKickoff &&
                    site.Assembly == "DiagnosticsVisualizer.Sample")
                { count++; break; }
            }
        }
        return count;
    }

    public static int VerifiedLifetimes(TraceView view)
    {
        var lifetimes = AsyncOperations.Analyze(view);
        var completed = lifetimes.Count(operation => operation.IsComplete && operation.Begin is { } begin &&
            view.SourceSites.TryGetValue(begin.NameId, out var site) && site.Assembly == "DiagnosticsVisualizer.Sample");
        if (completed == 0) throw new InvalidDataException("No completed, source-identified application async lifetime.");
        return completed;
    }

    public static int VerifyResourceMetadata(TraceView view)
    {
        var candidates = view.Snapshot.Properties.Where(p => p.Kind == "Resource candidate").ToArray();
        if (!candidates.Any(p => p.Name == "DiagnosticsLazy") ||
            !view.Snapshot.Relations.Any(r => r.Kind == "Merged dictionary"))
            throw new InvalidDataException("The resource inventory did not survive capture/transport.");
        foreach (var relation in view.Snapshot.Relations)
            if (relation.Source > 0 && relation.Target > 0 && (!view.Objects.ContainsKey(relation.Source) || !view.Objects.ContainsKey(relation.Target)))
                throw new InvalidDataException("Snapshot-only relationship metadata is missing.");
        return candidates.Length;
    }

    public static TraceView ReferenceInspection(TraceView view)
    {
        // Closing a real window may produce an empty final checkpoint. Inspect the retained last
        // populated state while separately reporting latest-node count; never alter viewer state.
        return view with { Snapshot = view.Checkpoints.LastOrDefault(c => c.Data.Nodes.Length > 0)?.Data ?? view.Snapshot };
    }

    public static object VerifyInvestigation(TraceView trace)
    {
        var routed = RoutedDispatches.Analyze(trace);
        var propagation = routed.SingleOrDefault(r => r.Name == "DiagnosticsRouting") ?? throw new InvalidDataException("Real routed propagation was not recorded.");
        if (propagation.Status != "Returned" || propagation.HandledAfter != false || propagation.ChangedHandled != 2 ||
            !propagation.Steps.Any(s => s.Status == "Skipped: handled") ||
            !routed.Any(r => r.Name == "DiagnosticsRoutingFailure" && r.Status == "Threw" && r.Steps.Any(s => s.Status == "Threw")))
            throw new InvalidDataException("Routed handled/skip/failure evidence is incomplete.");
        var routeEvents = trace.Events.ToDictionary(e => e.Id);
        var handlerIds = propagation.Steps.Where(s => s.Event.Kind == TraceEventKind.Span).Select(s => s.EventId).ToHashSet();
        var routedMutations = trace.Events.Count(e =>
        {
            if (e.Subsystem != Subsystem.PropertyNotifications) return false;
            var seen = new HashSet<long>();
            while (e.ParentId != 0 && seen.Add(e.Id) && routeEvents.TryGetValue(e.ParentId, out e))
                if (handlerIds.Contains(e.Id)) return true;
            return false;
        });
        if (routedMutations == 0) throw new InvalidDataException("Routed mutation lost its handler causal parent.");
        var origins = new XamlSourceIndex(trace).Origins;
        if (!origins.Any(o => o.Site.Document == "WorkloadWindow.axaml" && o.Site.Mapping == SourceMappingKind.XamlRoot) ||
            !origins.Any(o => o.Site.Document == "WorkloadWindow.axaml" && o.Type.Contains("Button", StringComparison.Ordinal) && o.Site.Mapping == SourceMappingKind.XamlConstruction))
            throw new InvalidDataException("Real compiled XAML root and Button origins were not captured.");
        var rows = DispatcherOperations.Analyze(trace);
        if (!rows.Any(r => r.Status == "Returned" && r.PostingThread != r.ExecutionThread) ||
            !rows.Any(r => r.Status == "Callback threw") || !rows.Any(r => r.Status == "Aborted before execution") ||
            !rows.Any(r => r.PriorityChanges.Length > 0 && r.InactiveQueueMs > 0 && r.RunnableQueueMs >= 0) ||
            rows.Any(r => r.Status == "Conflicting / invalid markers"))
            throw new InvalidDataException("Dispatcher entry/completion/fault/abort/priority/cross-thread evidence failed validation.");
        var starts = rows.Where(r => r.Started is not null).Select(r => r.Started!.Value.Id).ToHashSet();
        var linked = trace.Events.Count(e => e.Subsystem == Subsystem.PropertyNotifications && starts.Contains(e.ParentId));
        // A property notification can be nested below several synchronous method probes.
        var events = trace.Events.ToDictionary(e => e.Id);
        if (linked == 0) linked = trace.Events.Count(e =>
        {
            if (e.Subsystem != Subsystem.PropertyNotifications) return false;
            var seen = new HashSet<long>();
            while (e.ParentId != 0 && seen.Add(e.Id) && events.TryGetValue(e.ParentId, out e))
                if (starts.Contains(e.Id)) return true;
            return false;
        });
        if (linked == 0) throw new InvalidDataException("Callback mutations have no captured dispatcher parent chain.");
        return new { RoutedDispatches = routed.Length, RoutedSteps = routed.Sum(r => r.Steps.Length), RoutedCausalMutations = routedMutations, XamlOrigins = origins.Length, XamlObjects = origins.Select(o => o.ObjectId).Distinct().Count(),
            DispatcherOperations = rows.Length, Returned = rows.Count(r => r.Status == "Returned"), Faulted = rows.Count(r => r.Status == "Callback threw"),
            Aborted = rows.Count(r => r.Status == "Aborted before execution"), Priorities = rows.Sum(r => r.PriorityChanges.Length), CausalNotifications = linked };
    }


    public static object VerifyResourceLookups(TraceView trace)
    {
        var index = new ResourceLookupIndex(trace);
        var roots = index.Roots;
        ResourceLookup Required(string key, string status) => roots.FirstOrDefault(r => r.Key == key && r.Status == status)
            ?? throw new InvalidDataException($"Missing resource outcome {key}: {status} in actual capture.");
        var shared = Required("DvLookupShared", "Found");
        var themed = Required("DvLookupTheme", "Found");
        _ = Required("DvLookupNull", "Found null");
        _ = Required("DvLookupMissing", "Missing");
        _ = Required("DvLookupThrows", "Threw");
        if (themed.Theme != "Dark" || shared.ValueId == 0 || !roots.Any(r => r.Key.Contains("opaque key", StringComparison.Ordinal)))
            throw new InvalidDataException("Resource key/theme/value identities were not retained.");
        var calls = index.Descendants(shared.Id);
        if (calls.Length < 3 || !calls.Any(r => r.Call.Status == "Missing") ||
            !calls.Any(r => r.Call.ParentLookupId != 0 && r.Call.ValueId == shared.ValueId && r.Call.ProviderId != shared.ProviderId))
            throw new InvalidDataException("The actual resource delegation path was not retained.");
        var deferred = roots.Where(r => r.Key == "DvLookupDeferred").ToArray();
        if (deferred.Length != 2 || deferred.Any(r => r.Status != "Found" || r.ValueId == 0) || deferred[0].ValueId != deferred[1].ValueId)
            throw new InvalidDataException("Deferred resource caching evidence did not survive the capture path.");
        if (index.InvalidMarkers != 0) throw new InvalidDataException("Conflicting resource lookup markers.");
        foreach (var call in index.Calls)
        {
            foreach (var id in new[] { call.ProviderId, call.KeyId, call.ValueId })
                if (id > 0 && !trace.Objects.ContainsKey(id)) throw new InvalidDataException("Resource identity metadata is missing.");
        }
        return new { RootRequests = roots.Length, ProviderCalls = index.Calls.Length, index.InvalidMarkers,
            Found = roots.Count(r => r.Status == "Found"), FoundNull = roots.Count(r => r.Status == "Found null"),
            Missing = roots.Count(r => r.Status == "Missing"), Threw = roots.Count(r => r.Status == "Threw"),
            DelegationCalls = calls.Length, DeferredRequests = deferred.Length };
    }
}
