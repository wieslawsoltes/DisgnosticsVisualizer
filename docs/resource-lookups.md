# Recorded resource resolution

Alpha.9 records actual synchronous `TryFindResource`, `TryGetResource` and supported dictionary `TryGetValue` calls. It does not infer a winning value from candidate inventory or replay a lookup. The resource inventory in Properties remains a separate, non-materializing observation.

## Enable

```sh
AVTRACE_ENABLED=1 \
AVTRACE_RESOURCE_LOOKUPS=1 \
AVTRACE_FILE="$PWD/session.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

Clean or restore old instrumented output before rebuilding with the alpha.9 Build package. Rewriting remains confined to application output copies. Resource lookup capture is independent of dispatcher, routed-event, XAML, async-lifetime and ExecutionContext capture switches. No environment parsing runs on the hot path.

## What is observed

Each supported provider call records its key identity, requested theme key, provider identity, nesting parent, elapsed duration, and the original boolean/out-object outcome. The weaver checks the expected method contract before mutation and preserves the original body, exception handlers and returned value. It does not dereference an unspecified out slot after a false return or exception.

Outcomes distinguish **Found**, **Found null**, **Missing**, and **Threw**. A true/null result is not a miss. A found result names this provider call's actual result, not a hypothetical global winner. Nested calls show the observed logical-scope and dictionary delegation path, including misses preceding a success. Unsupported/custom providers may appear only through the instrumented caller; their missing inner history is not fabricated. The supported provider list and skipped-shape reasons are retained in the weaving manifest.

A lookup that constructs deferred content does so only because the application requested that lookup. The observer never invokes a deferred factory, performs a second lookup, formats a resource value, or calls arbitrary key `Equals`, `GetHashCode`, or `ToString`. Built-in immutable keys receive bounded labels; other keys are shown as opaque type/identity. Resource values are represented only by weak identity and type. Exception objects, messages and stacks are not recorded by this adapter. Printable resource keys can still contain application-specific information; review captures before sharing them.

## Causality and sampling

Whole top-level lookup trees are sampled per producer. An admitted tree retains nested observations even if recording is paused before it unwinds. A nested suppressed or unrecorded lookup shadows the previous resource context rather than incorrectly attaching observations to an outer call. Completion from a disposed session is not inserted into a later session. Synchronous resource scopes are thread-affine; arbitrary asynchronous resource resolvers are not represented as this contract.

Descriptor and completion markers can arrive in different batches. Analysis pairs them by exact 64-bit lookup identity, validates nesting, threads, identity agreement and interval consistency, and detects cycles or conflicting markers. Incomplete records remain incomplete. Known producer, metadata, transport or viewer-history loss is shown separately: a retained successful return does not prove the complete search path was captured.

Provider elapsed time is an inclusive wall-clock span. It can contain nested providers, deferred construction, waits and instrumentation overhead. Normal profiling subtracts the union of nested same-thread intervals rather than adding every provider duration as independent work. It is not GPU time or CPU utilization.

## Shared resource workspace

Choose **Workspace: Resources** or **Show Resource lookups** in the command palette. The Resources preset groups resource resolution, properties, dependencies and source navigation. It is additive; existing saved workspace layouts and preset values retain their meaning.

The request table searches key, theme and provider, filters outcomes, and can switch from root requests to all provider calls. The selected request's observed descendants appear in a common-scale cascade and virtualized table. Pointer/keyboard selection synchronizes with the trace event and navigation history. Provider/key/returned-object actions target the selected call; returned-object navigation is disabled for null, misses and unknown identities. **Analyze selected call** selects that call's exact recorded interval, not always the root's interval.

The cascade draws at most 96 retained calls and the table expands at most 8,192 calls per selected subtree. Omission labels and capture-health counters remain visible. The chart and provider table have an independent draggable splitter; action buttons wrap in narrow docking panes. Indexes are prepared with trace analysis and reused for selection. Virtualized lists avoid creating a control for each retained provider call.

Dock uses its actual compact Rider Dark preset with diagnostics-scoped editor/header/selection contrast. Floating diagnostics documents retain the palette without changing the embedding application's requested theme. Presets, panel rails, command navigation and persistence are shared by desktop and WebAssembly.

## Executable validation

Runtime tests cover warmed enabled/disabled allocation contracts, opaque keys, whole-tree sampling, paused completion, suppression and session isolation. A rewritten real Avalonia fixture verifies bool/out identity, merged/theme traversal, cached deferred construction, found nulls, misses and exception preservation. Protocol/analysis tests cover shuffled markers, malformed/cyclic parents, missing descriptors, capture loss and binary/Chrome export.

The packaged application contains no diagnostics API calls. Both local archive and TCP/encrypted-UDP/WebSocket runs require observed named resources, a real delegated search path with misses, the requested Dark theme, exactly two deferred requests returning the same object, found-null/missing/throw distinctions, and complete identity metadata. An opaque test key throws from `ToString`; the workload still succeeds. A separate unrequested deferred resource must remain unconstructed during inspection.

Headless UI tests exercise keyboard selection, actions on the selected provider call, exact range selection, filters and workspace restoration. Chromium activates the actual published resource workspace and retains existing routing, dispatcher, XAML, source, history, range and persisted-layout checks. The test workflow and retained reports determine which revision passed; passing these reference workloads is not a universal loss or overhead guarantee.

## Boundaries

This adapter does not capture every private binding subscription, every static/dynamic-resource invalidation dependency, all selector evaluation internals, or every custom provider. It does not enable native CLR profiler/ReJIT attachment or GPU timestamps/counters. Original output-copy instrumentation and strict opt-in capture remain required. NuGet packages are built and validated; publication requires the matching NuGet-side trusted-publisher policy and repository configuration.
