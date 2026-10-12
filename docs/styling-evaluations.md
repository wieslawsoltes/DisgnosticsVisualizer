# Styling evaluation and activation observations

Alpha.11 adds a managed, opt-in adapter for the supported Avalonia.Base styling shapes. It rewrites application-output assemblies and does not modify Avalonia source, package caches or application delegates. Restore or clean older instrumented outputs before upgrading; ordinary balanced/deep instrumentation remains available with the semantic feature disabled.

```sh
AVTRACE_ENABLED=1 AVTRACE_STYLE_EVALUATIONS=1 \
AVTRACE_FILE="$PWD/styling.avtrace" dotnet artifacts/instrumented/YourApplication.dll
```

## What the observations mean

The adapter reads the original return from instance `Selector.Match`, supported concrete selector `Evaluate` methods, `Style.TryAttach`, `StyleInstance.GetIsActive(out bool hasChanged)` and the existing activator sink's `OnNext(bool)` boundary. Match results preserve all five original categories. In particular, **Conditional (activator)** means the framework returned `Sometimes` and an activator; it does not assert that the activator is currently true. An immediate `subscribe=false` result keeps the framework's original category; the viewer does not reinterpret it as a permanent future guarantee.

Frame evaluation captures the actual returned Boolean and original `hasChanged` output. An incoming active/inactive signal is shown separately from the frame result. `Signal returned` means the existing sink callback returned normally, not that every setter won or a property changed. Exceptional unwinding is recorded without retaining exception objects or messages and without reading an unassigned output parameter.

Attachment attempts identify the style and control, with the call's host as context. Selector calls identify the selector and control, with the parent style as context. Frame/signal calls identify the style instance and its source style; the original activator identity links them to conditional results. **Shared frames do not necessarily have a unique control owner**. Their target remains unknown rather than calling the unsafe `Owner` getter or inventing a control. Neither matching nor active frames are proof of effective-value precedence: use recorded property inspection alongside these observations.

Original method/source identities survive recording and exports. Selector subjects can also have XAML construction origins captured separately. This adapter uses type/object identities, not arbitrary selector `ToString()` expressions; navigation to an object's recorded XAML origin remains a separate operation.

## Data integrity and overhead

Each admitted evaluation produces two small descriptor events and one completion span in the existing bounded binary ABI. Whole evaluation trees are sampled at their outer admission. Pausing recording allows admitted trees to complete; suppressed or unadmitted nested calls shadow the outer semantic context and cannot leak children into it. Old-session scopes cannot attach children to replacement sessions. Failures are contained and counted. Original target/subject/context/activator identities are weak and metadata is bounded by the existing capture options.

Descriptors carry no elapsed cost. Completion spans measure synchronous wall time, including any waits, not CPU utilization, asynchronous completion or GPU time. Known incomplete capture counters are carried into analysis. Missing pairs have unknown duration/outcome, conflicting descriptors and cyclic/impossible parentage are exposed, and repeated exports preserve source identities and integrity provenance. No missing selector or branch is inferred to have run.

Warm default-mode probes and the new enabled/paused adapter paths have exact zero-byte regression tests inside fixed, GC-isolated test intervals. Cold metadata/thread registration allocates. Optional ExecutionContext flow still adds its separate AsyncLocal overhead; the production adapter never suppresses GC. The comprehensive reference capture uses 131,072 events per producer thread to accommodate synchronous startup with all semantic adapters enabled; this is not a default capture-profile change or a universal loss guarantee.

## Shared workbench

Use **Workspace: Styling**, **Show Styling evaluations** or **Show Style activation history** in the command palette. The document supports root/all/selector/attachment/frame/signal modes and type/method/outcome search. Keyboard/pointer selection of the common-scale cascade synchronizes the call table and global event selection. Target, subject, context, activator and source actions operate on the selected call; unavailable identities disable navigation. Analyze uses that call's exact retained half-open interval.

The cascade shows at most 96 calls and the subtree table at most 8,192, with explicit truncation notices. Related activation history is a bounded merge of pre-indexed chronological frame/style/activator identity lists, not a whole-trace scan on selection. It shows timestamps and observed state changes without filling gaps or assuming every frame observation concerns the selected target. Invalid or partial data never produces a fabricated complete interval. The separate property-precedence inspector is not replaced by this document.

The additive Styling preset keeps prior preset numeric identities and stored docking topology compatible. Action bars wrap; cascade/table and request/detail splits are resizable; palette overrides remain scoped to the workbench. No application theme is changed.

## Verification and remaining scope

Tests compare the original and actually rewritten Avalonia framework on type/name/class matches, conditional matches, class activation/deactivation/reactivation, a hostile style formatter and a null-target exception. Other tests cover whole-tree sampling, paused completion, suppression, session replacement, pair reordering, incomplete/cyclic/conflicting records, retained identity history and binary/Chrome exports. UI tests exercise actual selection, keyboard input, action targets, activity history, filtering and docking restore. The packaged application uses public styling APIs to verify active opacity, a local-value override, clearing it, and deactivation, locally and through the collector.

Current supported shapes are checked on the pinned Avalonia 11.3.22 dependency. The adapter does not cover every container-query path, custom selector/provider internals, setter application history, binding expression states, or live style editing. Native profiler/ReJIT attachment, native GPU timing/counters and exhaustive XAML setter mappings remain separate work. No claim of complete original DevTools scope or NuGet.org publication follows merely from passing these tests.
