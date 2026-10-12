# Binding invocations and dynamic-resource lifecycles

Alpha.12 adds identity-only observations of the pinned Avalonia 11.3.22 binding pipeline. It instruments application output copies; Avalonia sources and package caches are not changed.

```sh
AVTRACE_ENABLED=1 \
AVTRACE_BINDING_OPERATIONS=1 \
AVTRACE_FILE="$PWD/bindings.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

Clean or restore older instrumented output before updating the Build package. The switch is independent of ordinary binding spans, automatic ExecutionContext propagation, dispatcher lifecycles and resource-lookup trees. Enable those other adapters to see their respective causal/lookup evidence as well.

## Observed boundaries

The adapter records existing `AttachCore`, `Start`, `Stop`, `Dispose`, `PublishValue`, `UpdateSource`, `UpdateTarget`, `WriteValueToSource`, `Convert`, `ConvertBack`, `OnNodeValueChanged`, `OnNodeError` and `OnDataValidationError` calls on the supported untyped binding implementation. It observes sink `OnChanged` and `OnCompleted` at their actual original call sites, including the dispatcher-posted closure. It does not substitute the sink, wrap callbacks, call the converter twice, or cause an additional binding subscription.

Dynamic-resource observations include original host subscribe/unsubscribe method calls, resource invalidation callbacks, theme/owner change callbacks and resolution attempts. These reference the actual resource expression, resource-key identity and host reference. Resource lookup trees are a separate adapter: a resource resolution call is not itself proof of which dictionary supplied a value.

The identity and state fields are deliberately narrower than arbitrary object inspection:

- Expression, recorded attachment target/property and participant (sink, converter or resource key) identities.
- Existing input/result references, without value contents or formatting. Null, UnsetValue, UnchangedValue and DoNothing are classified by reference equality against the original framework sentinels.
- Original bool results for source writes; original converter result and BindingError kind. Error messages, exception objects and binding descriptions are not collected.
- Original running-state getter observations before/after calls; original delivery valueChanged/errorChanged arguments; Start's requested produceValue argument; node index or attachment priority when supplied.

The known getters must be inert field loads, enum layout must match, and sentinel fields must be immutable references. Unsupported shapes are reported instead of guessed. Normal return records outputs; exceptional unwinding records Threw without reading an unassigned output. A throwing application converter can still trigger Avalonia's original error handling and formatting; diagnostics does not add those calls.

## Measurement interpretation

A method invocation is not a promised state transition: Start may already be running, Stop/Dispose may be no-ops, and WriteValueToSource can return true when no assignment is needed or DoNothing was returned. PublishValue's output describes the expression's observed stored value after the original method returns. Re-entrancy can have changed that value; it is not a synthesized payload for every earlier delivery.

Publication and delivery are separate operations. Publication on a background thread may return before a UI-thread sink callback starts. Correlate the captured dispatcher/causal chain where present. Repeated input identity alone does not prove that a particular publication caused a particular delivery; the viewer therefore does not invent publication-to-delivery latency or edges from value equality. The reference integration uses a unique object published once to verify the actual cross-thread boundary.

A completed delivery callback does not prove that its value won property precedence. A successful attachment target remains a *historical recorded attachment*, not a live subscription claim after disposal, reattachment, missing data or collection. Theme/resource callbacks are observations at those boundaries, not an exhaustive dictionary invalidation journal or a continuous subscription-state reconstruction.

Span durations are elapsed invocation time, including waits in the invocation. Descriptor events do not contribute additional span cost. Asynchronous lifetimes are separately represented by the async adapter. Missing descriptors, completions, conflicting phases, cycles and impossible nesting remain incomplete/invalid. Known loss and retention eviction are disclosed.

## Data, indexing and overhead

Each sampled invocation has three small descriptor records and one completion span. The descriptors identify the expression/target, property/participant and input/detail. The completion preserves its original invocation ID, causal parent, elapsed time, outcome and output identity. This keeps existing framing, varint/delta encoding, TCP/UDP transport, snapshot checkpoints and binary export compatible. Older readers can still display generic spans but do not understand the added semantics. Chrome export includes named binding fields and raw input/output fact masks; it never embeds the underlying value text.

Sampling admits a complete synchronous binding-call tree, not each level independently. Nested suppression shadows the recorded parent. Admitted calls may complete after pause; scopes from a disposed session cannot acquire a new session's children. Weak object identities and existing metadata/buffer capacities apply. Cold registration allocates and optional ExecutionContext propagation has separate AsyncLocal costs. Strict zero-byte checks cover warmed GC-isolated test intervals, not the whole application or arbitrary GC activity.

Analysis builds the invocation forest and per-expression chronological lists once per published trace. Selection uses these indexes. Lists are bounded to 8,192 displayed rows and the chart to 96 calls; omitted data is not represented as absent activity. Display limits do not alter the retained recording.

## Shared workbench

Use **Show Binding operations** or **Show Binding expression history** in the command palette. Bindings also appears when reapplying the Styling, Resources or Investigate preset. Older saved layouts retain the newly registered document on demand. The view supports filters, resizable request/detail and timeline/table splits, pointer/keyboard timeline selection, source navigation, exact selected-call range analysis and navigation to recorded identities. Missing target metadata disables target navigation; incomplete invocations cannot create a fabricated analysis range.

The reusable docking factory accepts additional documents before mounting with `RegisterDocument`. Identities are validated, document controls remain single-owned through the existing lease host, and the diagnostics-scoped Rider theme is retained in native and managed floating hosts.

## Validation

Runtime/index tests cover hostile value methods, sentinels, pause/sampling/suppression, session replacement, reordered/missing/duplicate/cyclic markers, binary re-export and the strict allocation gate. Rewritten-framework tests execute public Avalonia bindings and compare capture-on/off converter counts plus explicit expected behavior: source updates, two-way writes, null, DoNothing, fallback, missing nodes, resource refresh and disposal. A compatibility test changes the state getter and verifies semantic-hook rejection.

The actual packaged headless application also publishes a unique object from a background thread to a UI-thread sink and changes dynamic-resource theme values light → dark → light. Local-file and TCP/encrypted-UDP/WebSocket capture verifiers require binding evidence, existing subsystem checks and preserved application outcomes. UI tests navigate an earlier recorded target and restore the docking layout. Browser checks exercise document/history activation and persistence.

These adapters do not provide native CLR profiler/ReJIT attachment, GPU counters, exhaustive arbitrary binding plugins/compiled generic expression internals, every custom resource provider, or complete XAML setter-to-IL maps. Those are separate capabilities; no native/GPU support is implied by these managed observations. NuGet packages are validated by CI; account-side trusted-publisher policy and repository environment configuration are still prerequisites to a NuGet.org upload.
