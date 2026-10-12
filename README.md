# Diagnostics Visualizer

Cross-subsystem Avalonia recording, analysis and a shared desktop/WebAssembly Rider docking workbench. Instrumentation rewrites **application output copies**, not Avalonia sources or package-cache assemblies.

[Browser workbench](https://wieslawsoltes.github.io/DisgnosticsVisualizer/) · [Build and integration](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/build.yml) · [Browser verification and deployment](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/browser.yml)

**0.1.0-alpha.12.** The browser starts with an explicitly labelled synthetic example, not a live attachment. This is an actively validated implementation, not a claim that every Avalonia release, native platform backend or DevTools capability is covered.

## New: binding invocations and dynamic-resource lifecycles

Enable `AVTRACE_BINDING_OPERATIONS=1` on instrumented output to record supported binding attachment/start/stop/disposal, publication, actual sink delivery, conversion and source-write results, node errors and dynamic-resource subscribe/unsubscribe/invalidation/theme/owner callbacks. Original delegates, converters, sink instances and values are preserved. Null, UnsetValue, UnchangedValue and DoNothing are distinguished without formatting values or replaying binding evaluation.

**Show Binding operations** and **Show Binding expression history** open the shared document. Styling, Resources and Investigate presets include Bindings when reapplied; older layouts keep it available on demand. Resizable request/detail and timeline/table splits, filters, keyboard/pointer selection, source navigation, recorded identity navigation and exact selected-call analysis all use the recorded trace. A historical attachment is labelled as such; a publication is not represented as a completed delivery or winning effective value. See [binding lifecycle contracts and validation](docs/binding-lifecycles.md).

## Existing workspaces and semantics

The actual **Dock Rider Dark compact** theme is scoped to diagnostics and its detached documents. It does not force an embedding application to use a dark palette. Tab splitting, pinning, managed/native floating windows, independent panel controls and persistent workspace topology are shared between desktop and browser. Invalid, oversized, deeply nested or duplicate saved layouts are rejected before live mutation. Settings contain panel identities/geometry, not trace content or credentials.

The workbench includes synchronized subsystem timelines/thread flame charts, selected-event causality, range statistics, top-down and bottom-up calling contexts, thread span coverage, baseline comparisons, visual/logical trees, property/precedence/resource inspection, layout geometry, checkpoint history/differences, bookmarks, capture health, queryable event logs and evidence-labelled findings.

Dedicated semantic documents cover:

| Area | Recorded evidence | Contract |
| --- | --- | --- |
| Styling | Original selector results, attachment attempts, frame activity/hasChanged and activator signals | [Styling](docs/styling-evaluations.md) |
| Resources | Executed provider-call trees, key/theme identities, found/null/missing/throw outcomes | [Resource resolution](docs/resource-lookups.md) |
| Routed events | Direct/Tunnel/Bubble, handler/class-listener calls, skipped candidates, handled states and exceptions | [Routed events](docs/routed-events.md) |
| Dispatcher | Admission, priority transitions, inactive/runnable residency, invocation, return/throw/abort | [Dispatcher and XAML](docs/xaml-dispatcher.md) |
| XAML and sources | Original PDB sequence points, construction/root-population identities, checksum-verified source navigation | [Sources and causality](docs/source-causality.md) |
| Async operations | Standard state-machine entry/terminal transitions, separate from synchronous kickoff and causal links | [Async lifetimes](docs/async-lifetimes.md) |
| Bindings | Original lifecycle calls, publication/delivery, converters, source writes and dynamic-resource callbacks | [Bindings](docs/binding-lifecycles.md) |

Conditional selector matches, active frames and winning property values remain distinct. Class/multicast listener timings are aggregates, not individual subscriber timings. Resource lookup outcomes do not expose every custom provider's private path. Snapshot resource inventories do not materialize deferred values and are not labelled as guaranteed lookup winners.

Drag the span-activity overview to select a half-open range. Percentiles use observed range-clipped durations. Exclusive time subtracts the **union of same-thread child intervals**, not asynchronous continuation time. Span coverage includes waits and is **not CPU utilization**. Managed rendering/compositor spans are not GPU timestamps. Layout geometry is based on recorded bounds, not unrecorded transforms/clips.

Event queries support Boolean expressions, exact 64-bit identifiers and duration units:

```text
subsystem:Layout duration>=1ms
(name:Measure or name:Arrange) not thread=8
object=12 duration>500us
```

`Ctrl+Shift+P` opens commands; `Ctrl+O` imports, `Ctrl+S` saves, `Ctrl+L` connects, `Ctrl+B` bookmarks, `F12` reveals source, `F8` pauses/resumes a connected target, and `Ctrl+Shift+F` toggles focus mode. Alt+Left/Right navigates bounded event/object selection history. Pan timelines by dragging, zoom with Ctrl+wheel and double-click to fit. Bookmarks currently belong to the loaded session.

## Build and run

Reusable libraries target .NET 8. Tools/tests/desktop use .NET 10; the browser uses .NET 10 WebAssembly. The pinned lane is Avalonia **11.3.22**, `Dock.Avalonia.v11` / `Dock.Avalonia.Themes.Fluent.v11` **11.3.22**, and `Dock.Model.Mvvm` **12.1.0.7**. Do not mix unsuffixed Avalonia 12 UI packages into this build.

```sh
dotnet build DiagnosticsVisualizer.slnx -c Release
dotnet test tests/DiagnosticsVisualizer.Tests -c Release --no-build
dotnet test tests/DiagnosticsVisualizer.UiTests -c Release --no-build
dotnet run --project src/DiagnosticsVisualizer.Desktop -c Release
```

```sh
dotnet workload install wasm-tools
dotnet publish src/DiagnosticsVisualizer.Browser -c Release -o artifacts/browser-publish
python3 eng/prepare-browser.py
python3 -m http.server 8080 --directory artifacts/site
```

Staging preserves fingerprinted SDK assets and uses the actual published loader. The browser is a viewer, not an IL-instrumented application target.

## Instrument application output

Build the local feed with `bash eng/pack.sh`. Until NuGet publication, add the absolute `artifacts/packages` directory as a restore source. Add a diagnostic-only reference to the application or an imported project file:

```xml
<ItemGroup>
  <PackageReference Include="DiagnosticsVisualizer.Build" Version="0.1.0-alpha.12" />
</ItemGroup>
<PropertyGroup>
  <DiagnosticsEnabled>true</DiagnosticsEnabled>
  <DiagnosticsAllowSignedRewrite>true</DiagnosticsAllowSignedRewrite>
  <DiagnosticsDeepProfile>false</DiagnosticsDeepProfile>
</PropertyGroup>
```

Package reference alone does **not** enable weaving. Keep diagnostic outputs separate from shipping binaries:

```sh
dotnet publish YourApplication.csproj -c Release -o artifacts/instrumented \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true
AVTRACE_ENABLED=1 AVTRACE_FILE="$PWD/session.avtrace" \
  dotnet artifacts/instrumented/YourApplication.dll
```

The capture file must not already exist. The injected module initializer is inert without `AVTRACE_ENABLED=1`. Recording starts early; tree inspection waits for platform initialization. Framework and entry assemblies are candidates by default; `DiagnosticsIncludeAssemblies` includes other application libraries. `DiagnosticsDeepProfile=true` adds application control overrides, selected getters, application async kickoff methods and supported compiled-XAML source sites. Keep portable PDBs beside inputs during weaving.

Signed identities require explicit diagnostic-only signature invalidation or `DiagnosticsSigningKey`. Never distribute invalidated signatures as production binaries. Package-cache/shared-runtime paths and native/ReadyToRun images are rejected. MSBuild also rejects trimmed, NativeAOT, ReadyToRun, single-file and browser target instrumentation.

`avtrace.manifest.json` records original/output hashes and MVIDs, actual hook matches, skips and bootstrap status. Originals remain in `.avtrace/original`. Stop the target before restoring with `RestoreDiagnosticsOutput` or the embedded weaver's `restore --directory <output>`. Restoration checks hashes and replaces files rather than truncating mapped DLLs. **Clean/restore before updating the package or changing the instrumentation profile.**

Semantic capture switches are independent and opt-in:

```sh
AVTRACE_ENABLED=1 \
AVTRACE_XAML_OBJECTS=1 \
AVTRACE_DISPATCHER_OPERATIONS=1 \
AVTRACE_ROUTED_EVENTS=1 \
AVTRACE_RESOURCE_LOOKUPS=1 \
AVTRACE_STYLE_EVALUATIONS=1 \
AVTRACE_BINDING_OPERATIONS=1 \
AVTRACE_ASYNC_OPERATIONS=1 \
AVTRACE_FLOW_EXECUTION_CONTEXT=1 \
AVTRACE_FILE="$PWD/deep.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

All-adapter startup bursts may require explicitly larger buffers. Defaults are not sized as a universal zero-loss guarantee. [Capture profiles](docs/capture-profiles.md) describe capacities, masks and sampling.

## Remote and in-process capture

After building, generate a random collector token. Keep credentials in environment variables, never endpoint URLs:

```sh
COLLECTOR=src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll
export AVTRACE_TOKEN="$(dotnet "$COLLECTOR" --new-token)"
dotnet "$COLLECTOR" --archive ./captures
```

Launch instrumented output with the same token:

```sh
AVTRACE_ENABLED=1 AVTRACE_ENDPOINT=tcp://127.0.0.1:4318 \
AVTRACE_UDP_ENDPOINT=127.0.0.1:4319 \
dotnet artifacts/instrumented/YourApplication.dll
```

Connect the viewer to `ws://127.0.0.1:4317/trace` and enter the token. Omit the UDP endpoint for reliable-only delivery. Session establishment, control, snapshots and oversized batches remain reliable; eligible event batches switch to encrypted UDP only after authenticated acknowledgement. Nonce sequences survive reliable-channel reconnects. UDP is best effort and missing batches remain visible.

Remote endpoints require TLS/WSS and a trusted certificate; plaintext is loopback-only. The collector validates exact browser origins, authenticates peers, bounds queues and reassembly, limits commands and disconnects slow viewers. It accepts one source session at a time. Configure `--http`, `--tcp`, `--udp`, `--origin`, `--certificate`; use `AVTRACE_CERT_PASSWORD` for PFX passwords. GitHub Pages hosts the static viewer, **not the collector**. HTTPS-hosted pages may require WSS even for loopback.

For in-process hosting, reference Agent.Avalonia and Workbench, call `WorkbenchStyles.Install` on the initialized application, and keep the leases alive:

```csharp
await using var host = DiagnosticsHost.Start(new AgentOptions());
await using var subscription = host.Subscribe();
await using var model = new WorkbenchModel(loadDemo: false);
await model.AttachInProcessAsync(subscription.ReadAllAsync(),
    command => Task.FromResult(host.Execute(command)));
using var view = new WorkbenchView(model);
// Mount on the UI thread. Exclude the viewer root from target inspection.
```

`Inspector.AttachRoot` supports explicit roots; `Inspector.ExcludeRoot` excludes diagnostic UI from snapshots, not its rendering overhead. Prefer out-of-process viewing for target-performance investigations. See [embedding](docs/embedding.md).

## Data integrity and overhead

The runtime uses bounded per-thread rings, weak identities, cached metadata, sampling and subsystem masks. Initial registration allocates. Optional ExecutionContext flow adds AsyncLocal cost. Strict allocation gates cover warmed, GC-isolated test intervals; the production recorder does **not** suppress garbage collection. File/network work is off the target UI thread. Snapshot traversal yields between slices; custom getters can exceed a slice budget. Inspector work is suppressed from ordinary probes and recorded separately as diagnostics overhead.

Snapshots are bounded weakly consistent observations, not atomic event-time states or heap dumps. Visual/logical/template parents remain separate; absence in a truncated snapshot is unknown. History retains up to 32 checkpoints/65,536 nodes. Reliable streams negotiate baseline-hashed snapshot patches and per-connection full checkpoints, with legacy full-frame fallback; UDP event batches remain stateless. [Stateful protocol](docs/STATEFUL-PROTOCOL.md).

Arrival-bounded retention trims only the required prefix and preserves oversized-batch tails. Incremental chronological publication sorts new arrivals and merges them with retained events, rather than re-sorting the entire recording. Public event arrays remain independently owned. Retired sequence gaps remain accounted for, and old replay cannot re-enter after retirement. Very old gaps cannot be repaired after the bounded horizon. Binary exports preserve original loss and viewer-eviction provenance.

Source navigation retains original module/method identities, PDB checksums and validated SourceLink metadata. Source text is read only from explicitly chosen files and is not exported. No trace-supplied file path or URL triggers automatic reads or fetches. Existing public SnapshotData arrays retain their ownership contract; this is not a newly deeply immutable trace API.

## Reusable projects and validation

Ten NuGet packages: **Core, Protocol, Runtime, Analysis, Transport, Agent.Avalonia, Charts, Controls, Workbench, Build**, each under the `DiagnosticsVisualizer` prefix. The Mono.Cecil Weaver is embedded in Build. Collector is a separate executable; App is shared; Desktop/Browser contain platform entry points. Instrumentation, transports, analysis, charts and controls can be used independently.

CI builds/tests all projects, verifies ten packages and executes a real headless Avalonia application consuming those packages. Its source has no diagnostics API calls. Local-file and TCP/encrypted-UDP/WebSocket runs verify original application results, semantic evidence, loss accounting and a viewer control acknowledgement. Reports, logs and headless screenshots are retained. Published WebAssembly is exercised in Chromium with keyboard input, range selection, document navigation, workspace reload, compact resizing and console/network checks. New binding-specific browser checks cover document/history activation and persistence; populated binding semantics are tested by headless UI fixtures and actual packaged capture, not invented as a live browser session.

The ingestion benchmark includes checksum and retention-count verification without a shared-runner speed threshold:

```sh
dotnet run --project tests/DiagnosticsVisualizer.Benchmarks -c Release -- artifacts/performance/ingestion-report.json
```

Measurement results are workload-specific. No universal overhead percentage, zero-loss promise, CPU-utilization inference or native-GPU timing claim is made.

## Remaining capabilities

Not yet implemented: arbitrary-process native CLR profiler/ReJIT attachment; GPU timestamp/counter backends; exhaustive custom scheduler/plugin/provider semantics; every private compiled-binding/accessor path; complete setter-application and XAML element-to-IL mapping; heap-retention proof. Hook matches are not proof of execution—use the manifest together with a workload capture.

## Publishing

The browser workflow verifies the application before Pages deployment through the `github-pages` environment. It attempts Pages enablement; repository policy may require GitHub Actions Pages configuration or an appropriately scoped `PAGES_TOKEN`.

`release.yml` builds, tests, packages and runs integration before uploading. It uses **NuGet trusted publishing** (`NuGet/login@v1`, GitHub OIDC), not a long-lived API key. Register a NuGet policy for owner `wieslawsoltes`, repository `DisgnosticsVisualizer`, workflow `release.yml`, environment `nuget`; set repository `NUGET_USER` to the NuGet profile and create the environment. The workflow cannot create the account-side policy. Push `v<version>` or dispatch an exact version after configuration. A validated package artifact is not evidence of NuGet.org publication.

MIT licensed; dependencies retain their respective licenses.
