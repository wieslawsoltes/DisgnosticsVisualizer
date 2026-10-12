# XAML origins and dispatcher investigation

Alpha.7 adds opt-in managed adapters without changing Avalonia sources, NuGet cache assemblies, callback delegates, or application tasks. The Build package rewrites output copies in deep mode. Keep portable PDBs beside the inputs before weaving and restore or clean old instrumented output before upgrading.

## Enable recording

```sh
AVTRACE_ENABLED=1 \
AVTRACE_XAML_OBJECTS=1 \
AVTRACE_DISPATCHER_OPERATIONS=1 \
AVTRACE_FILE="$PWD/session.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

These switches are independent from `AVTRACE_FLOW_EXECUTION_CONTEXT` and `AVTRACE_ASYNC_OPERATIONS`. Queued callbacks restore an explicit admission context at invocation even when the framework intentionally does not flow ExecutionContext; ordinary arbitrary schedulers are not intercepted by this adapter.

## XAML source semantics

The weaver reads the compiler's visible `.axaml` / `.xaml` portable-PDB sequence points before any edits. For supported reference-type `newobj` instructions it duplicates the successful construction result, records the object/source identity, and leaves the original evaluation stack intact. A throwing constructor produces no successful-construction marker. Supported compiled `XamlIlPopulate` methods also identify the root passed to population; this is not represented as a new allocation or as successful completion of population.

Sites retain original assembly identity, MVID, MethodDef and IL offset, document basename, source range, document checksum and a validated SourceLink URL where available. Repeated factory invocations create separate runtime object identities at the same static site. Value types, unsupported System/closure constructors, missing symbols and hidden sequence points do not acquire guessed locations. Deferred factories are instrumented but never invoked by inspection.

The XAML origins document searches observed file/type/location data and navigates to a selected source site. A tree selection with no explicit event resolves that object's latest retained origin. An explicit event selection takes precedence. This is not a transactional correspondence to a historical snapshot. Re-population may create multiple origins; missing/sampled/evicted observations remain unknown. The viewer does not reconstruct XML, associate every setter/binding with an element, or claim exact start-tag ranges when the compiler emitted coarser sequence points.

Source text stays local: only an explicitly chosen bounded UTF-8 file is read, and a present PDB checksum must match the raw bytes. Trace-supplied paths/URLs never trigger reads or network requests. Binary exports retain the source metadata; they do not embed source text.

## Dispatcher semantics

The adapter validates the complete known Avalonia operation/priority-queue/callback shape before mutation. Admission is recorded after queue insertion, within the framework's queue synchronization. Captured operations retain all observed priority changes, invocation, return/throw and successful abort markers, even when admission sampling or recording controls subsequently change. A callback exception is observed in the existing catch path without keeping its object or touching Task.Exception/Result. Existing exception handling, task identity and scheduler priorities are preserved.

- Queue residence: admission to callback start, or admission to abort. It includes inactive timer residence.
- Runnable wait: the sum of observed queue intervals whose priority exceeds the framework's Inactive priority.
- Inactive residence: the remaining observed queue intervals. It is not diagnosed as UI starvation.
- Callback duration: invocation to its terminal return/throw observation. A callback returning an awaitable can continue asynchronously afterwards.

Intervals are wall-clock latency, not CPU utilization or GPU time. Queue phase markers are not synchronous spans and do not add themselves to span self-time. Cross-thread causal links identify observed callback admission, not proof of a root cause.

The queue document filters outcomes and site names, shows a six-row common-scale diagram of the longest retained queue residences, and supports event/analysis-range navigation. Inactive intervals are gray, runnable intervals amber, and callback intervals blue. Null measurements stay absent rather than being drawn as zero. Marker conflicts are identified; incomplete rows are retained. Any known producer/transport/metadata/eviction loss prevents claiming an exact runnable/inactive split, even when a residence interval can still be measured.

## Performance and embedding

XAML observations use existing sampled event buffers and bounded metadata. Dispatcher admission allocates a small capture record for a sampled operation; weak keys avoid retaining queued work items. Metadata capacity bounds registration. A callback scope holds its operation only during invocation. Both features are opt-in; warmed disabled probe allocation tests remain strict. Automatic ExecutionContext propagation has separate AsyncLocal costs.

Analysis prepares dispatcher rows and the XAML index once per trace analysis. The workbench reuses those results instead of rescanning every event on selection. Table rendering uses the existing virtualized DataGrid; retained trace/history budgets still apply.

The actual Dock Rider Dark palette is scoped to the workbench and its detached documents/native hosts. Installing diagnostics no longer sets the application's requested theme or invokes the global Dock preset switcher. Base control themes are installed if needed; the workbench's visual overrides are restricted to its own scope. Inspect/Profile/Compare identities retain their numeric values; Investigate is additive, and unknown older-layout panels are available on demand.

## Validation and remaining capabilities

Executable tests cover constructor failure, repeated XAML instances, root identity, binary source re-export, strict warmed allocation gates, dispatcher abort/fault/priority changes, incomplete/loss semantics, scoped light-host embedding and real managed/native docking. The packaged sample uses actual compiled Avalonia XAML and real cross-thread UI callbacks without diagnostics API calls, and runs locally and through TCP/encrypted UDP/WebSocket with zero-loss reference-workload gates. Browser checks activate Investigate and the new documents through real keyboard input and preserve existing source/range/persistence tests.

This is a version-shape-checked managed adapter, not arbitrary CLR profiler/ReJIT attachment, native GPU timestamp/counter support, exhaustive private style/resource evaluation history, or exhaustive custom/unsafe scheduler interception. Unsupported framework shapes are reported in the weaving manifest rather than instrumented partially. NuGet packages are built and validated; trusted publication still needs the account-side publisher policy and repository configuration.
