# Routed propagation and investigation navigation

Alpha.8 adds opt-in routed-event semantics to output-copy instrumentation. It does not change Avalonia source, the package cache, the route loop's ordering/predicates, application delegate instances, or exception handling.

```sh
AVTRACE_ENABLED=1 \
AVTRACE_ROUTED_EVENTS=1 \
AVTRACE_FILE="$PWD/session.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

Clean or restore output instrumented with an older hook ABI before rebuilding. The supported adapter is shape-checked against the compiled framework. The weaving manifest records two `RoutedEventDispatch` entries for recognized dispatch and route-loop shapes. Unknown or ambiguous shapes are skipped before semantic IL mutation; ordinary method-span coverage is independent.

## Recorded semantics

A dispatch has a begin marker and a synchronous completion span sharing a dispatch identity. A sampled dispatch retains its class-notification, instance-handler, and skipped-candidate observations without separately sampling each step. Nested dispatches have independent identities, even when application code reuses an event-args instance. A nested raise not selected by sampling shadows the outer scope so its handlers cannot be misattributed.

Each invocation records its target identity, current source identity at entry, Direct/Tunnel/Bubble phase, before/after `Handled`, registered `handledEventsToo`, elapsed invocation duration, and normal-return versus exceptional-exit state. Instances skipped by the original handled/route predicates have explicit zero-duration observations; they are not counted as invoked handlers. The original callback is invoked only once. The original typed-adapter exception or `DynamicInvoke` wrapper exception is propagated unchanged, including original null-argument guards at dispatch entry.

Class-listener notifications and multicast delegate invocations are **aggregate boundaries**. This adapter does not enumerate and time individual subscriptions to `RoutedEvent.Raised`, instrument individual members of an application multicast delegate, or record route-finished subscribers as separate semantic steps. Class notifications may run when an instance handler is skipped. `Handled` is not presented as "the entire route stopped": later `handledEventsToo` handlers can run and can clear it.

The payload includes no pointer coordinates, typed text, arbitrary event-argument fields, delegate target values, or exception objects/messages. Handler labels contain metadata type/method names (not guaranteed overload-unique source identities). Source navigation continues to use separately recorded method/PDB sites where available; handler labels do not invent source lines.

Handler spans participate in ordinary synchronous causal nesting, so property, resource, layout, dispatcher and application work can be followed from them. Optional `AVTRACE_FLOW_EXECUTION_CONTEXT=1` propagates causal context to ordinary flowing asynchronous work; it has its own AsyncLocal overhead. This is not exhaustive unsafe/custom scheduler interception. Returned async callbacks can finish later: callback duration ends at synchronous return, and the separate async lifetime adapter is used to inspect subsequent completion.

## Data integrity and visualization

The Routed events document provides searchable dispatches, outcome/handled-change filters, a full retained-step table and a common-scale, scrollable propagation chart. Tunnel spans use the style color, Bubble/Direct use blue, skipped candidates use gray ticks, and handled transitions are outlined. Exceptional exits are labelled. Class notifications can be hidden without changing recorded data. The chart renders at most 96 observed steps and discloses both displayed and total counts; the table retains all steps for that dispatch.

Selections synchronize with event details, properties, trees and timelines. Arrow keys move through chart observations. Inspect target selects the object; Analyze dispatch applies the measured synchronous interval. The Investigate docking preset includes routed events alongside dispatcher queue timing, XAML origins, dependencies and sources. New panels remain available on demand in older saved workspaces.

Missing begin/completion markers stay incomplete; contradictory or malformed markers stay invalid and are not visualized as a trustworthy duration. Producer loss, transport gaps, metadata loss, probe faults and viewer eviction are disclosed. Complete endpoint spans can establish elapsed duration even when intermediate steps are absent; absence of a step is not proof that a handler did not run. Duplicate ingestion is handled by normal event/frame identity de-duplication.

Binary re-export now preserves missing-batch and viewer-eviction counts across rebatching using an unused, bounded `AVLOSS1` metadata symbol. No target events are synthesized and subsequent export does not count the same inherited loss again. Additional gaps/eviction after import add to inherited counts with saturating arithmetic. Reserved metadata is validated before accepting its frame. Older readers remain framing-compatible but do not interpret these new provenance warnings; use alpha.8 or newer to inspect shared exports accurately. Chrome Trace JSON includes route fields and all integrity counters.

## Rider navigation and performance

Alt+Left / Alt+Right, the range-bar arrow buttons and command-palette actions navigate a bounded 128-entry event/object selection history. Selecting after going back discards the forward branch. New sessions reset history; missing event identities are skipped. History is transient and is not stored with workspace geometry. Back/forward changes viewer selection without issuing a remote inspect command.

The workbench continues to use the real scoped Dock Rider Dark compact palette; the new route tables and editor inputs share its spacing and selection styles. It does not change the embedding application's requested theme.

Enabled warm dispatch/handler scopes allocate no managed memory in the default non-flowing mode in the regression workload. First-time thread, object, delegate and symbol registration allocates. Delegate metadata uses weak keys and is constrained by capture identity budgets; it does not keep callback targets alive. Disabled semantic capture adds guard/scope work but no warm allocations. These statements are not a universal execution-time overhead guarantee. The route grouping pass runs during trace analysis, not on each selection; rendering and navigation are bounded.

## Verification

Executable tests rewrite and run the actual Avalonia route loop: Tunnel/Bubble/Direct ordering, handled reset, `handledEventsToo`, nested raises, typed and reflective exception contracts, original null guards, weak delegate targets, sampling isolation, pause/disposal, malformed/loss analysis, export and strict allocation tests. The packaged Avalonia sample exercises an actual visual route and property mutation without diagnostics API calls, both locally and via TCP/encrypted UDP/WebSocket. Headless and Chromium tests exercise the shared routed view, navigation, workspace persistence and existing diagnostics documents.

Native CLR profiler/ReJIT attachment, GPU timestamp/counter backends, exhaustive private style/resource evaluation history and full instruction/element maps are not implemented by this adapter. NuGet publication still requires the account-side trusted-publisher policy; package validation is not a claim of upload to NuGet.org.
