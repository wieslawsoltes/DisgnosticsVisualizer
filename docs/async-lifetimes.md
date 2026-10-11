# Async lifetimes and semantic inspection

## Capture contract

Enable deep instrumentation when building the diagnostic output and set `AVTRACE_ASYNC_OPERATIONS=1` when launching it. This is independent of `AVTRACE_FLOW_EXECUTION_CONTEXT=1`: the former captures elapsed lifetimes; the latter propagates causal parents. Both default to off.

Hook catalog version 4 recognizes C# compiler state machines using the standard Task, Task<T>, ValueTask, ValueTask<T> and async-void method builders. It adds a private token field, emits a begin marker on the first MoveNext entry, and an end marker immediately before the builder's terminal SetResult or SetException transition. The weaver manifest lists actual `AsyncLifecycle` matches and unsupported shapes. The hook never replaces a returned awaitable, reads its Result/Exception, awaits a ValueTask, or installs a continuation. A single-use IValueTaskSource must still be consumed only by the application.

These measurements include suspension and represent **state-machine entry to terminal transition**, not CPU utilization, scheduling delay, or a guarantee of the time at which every consumer continuation has executed. Existing kickoff spans still end at synchronous method return. A generated end marker does not turn its wait interval into a synchronous child span.

Fault/cancellation classification uses only the exception's type; no exception text or object is retained. One weak session reference is allocated per sampled operation. The added token contains no application object or Task references. Disabled first entries are remembered so resuming capture during an await cannot manufacture a new start. Captured completions may be emitted while recording is paused, but never into a different or disposed session. A volatile fast path avoids an interlocked write on subsequent MoveNext entries.

Custom builders, async iterators and nonstandard state-machine layouts are not instrumented by this adapter. A missing start/end can result from sampling, loss, retention limits or a stopped capture: the viewer reports unknown/incomplete rather than assuming a hung operation or zero duration. Traces continue using the existing Link wire kind with reserved lifecycle codes; legacy readers can retain the markers without interpreting their lifetimes. Chrome Trace JSON exports async b/e pairs separately from synchronous X events.

## Workbench

Use **Show Async operations** in the command palette or the right rail. The shared control correlates markers, filters outcomes/names, displays a bounded waterfall of the longest complete lifetimes and a virtualized table, and can select the operation's exact analysis interval. Invalid or conflicting markers are shown explicitly. The deterministic synthetic example is labelled DEMO and includes synthetic lifetimes.

**Show Dependencies** opens the object-reference neighborhood. Visual, logical and templated parents remain independent edge types. Recorded data contexts, collections, styles, themes, templates and resource dictionaries are navigable in either direction. Click a graph node or inspect a table row to continue traversal. This is not an event-causality graph or a heap-retention proof. The diagram is capped at 17 nodes/64 edges; the searchable table retains the selected object's complete recorded incidence list. Checkpoint selection also selects the graph's state.

The resource inspector inventories raw framework dictionary entries in local/logical ancestor/application scopes and merged/theme dictionaries without calling TryGetResource or building deferred values. Unknown resource-provider implementations remain graph nodes, not executed plugins. Inventory is capped at 32 logical scopes, 64 providers and 512 entries; partial inventory is disclosed and marks a captured checkpoint truncated. Candidates do not imply active theme selection or winning resource lookup. Existing property frames likewise report recorded activation/priority, not an invented winning setter. Select a value to filter its applied frames in Precedence.

Object metadata referenced only by checkpoints now travels with capture batches. Binary exports preserve retained checkpoint history (including empty shutdown observations) and large metadata closures through bounded metadata-only frames. Export batches are rebuilt around end-time watermarks; they are not a replay of original packet arrival order. Runtime per-frame metadata exhaustion increments the explicit metadata-loss counter.

The actual Dock Rider Dark compact preset is retained, with active panel-rail indicators and additional on-demand documents. **Ctrl+Shift+F** / **Toggle focus workspace** hides metrics/overview without rewriting the dock topology or saved preset. Focus mode is transient; saved layouts remain platform-shared.

## Validation and boundaries

Tests execute rewritten assemblies covering synchronous/asynchronous completion, generic state machines, faults, cancellation, async void, and single-consumption ValueTask sources. UI tests inspect real deferred resources, budgets, graph navigation and rendered documents. The packaged reference application contains no diagnostics calls and must retain completed source-linked lifetimes, resource candidates and snapshot-only metadata both locally and through the collector, without invoking its deferred resource factory.

This increment does not provide native CLR profiler/ReJIT attachment, GPU counters/timestamps, exact XAML element-to-IL mapping, exhaustive custom-scheduler causality or complete per-version private semantic adapters. Restore or clean previously instrumented outputs before upgrading the weaver. Actual NuGet publication still requires the configured trusted publisher account policy.
