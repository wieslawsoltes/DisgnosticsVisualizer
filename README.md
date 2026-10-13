# Diagnostics Visualizer

Cross-subsystem Avalonia recording, analysis and a shared desktop/WebAssembly Rider docking workbench. Instrumentation rewrites **application output copies**, not Avalonia sources or package-cache assemblies.

[Browser workbench](https://wieslawsoltes.github.io/DisgnosticsVisualizer/) · [Build and integration](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/build.yml) · [Browser verification and deployment](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/browser.yml)

**0.1.0-alpha.14.** The browser starts with an explicitly labelled synthetic example, not a live attachment. This is an actively validated implementation, not a claim that every Avalonia release, native backend or DevTools capability is covered. Check workflow results for deployment status.

## New in alpha.14: native CLR capture and streaming inspection

The optional native profiler attaches to an already running authorized .NET process,
ReJITs explicitly selected methods and records normal returns and exceptional unwinds
without editing assemblies on disk. Windows, Linux and macOS share the checked runtime
SDK adapter. This method-level path is separate from build-time Avalonia semantic weaving.

Open native `.dvntrace` files directly in desktop or browser. The **Native** docking preset
shows coverage, original method identities, thread-slot/OS-thread mapping and terminal
integrity, with explicit open/incomplete calls. Native provenance and UTC origin survive
binary export. The shared Rider layout remains isolated from an embedding application.

`NativeCapture relay` provides authenticated TCP/TLS forwarding of an append-only recording,
bounded reconnect replay, late-join metadata and explicit unsupported-control replies. The
browser uses the existing WebSocket gateway. `inspect` and atomic `convert` commands also
work without attaching a process. See [native build, commands and limits](native/README.md).

GPU timing/counters, exhaustive custom scheduler and XAML setter/candidate mappings remain
outside this release. Stopping native capture does **not** detach/revert the profiler. The
native binaries are workflow artifacts; the ten reusable NuGet packages remain separately
published through the existing trusted-publishing workflow.

## New: property operations and value history

`AVTRACE_PROPERTY_OPERATIONS=1` records supported original SetValue/SetCurrentValue/ClearValue/CoerceValue calls, direct SetAndRaise results, effective/base notifications, value-entry selection and setter-instance lifecycles. Requested values, notified values and effective precedence remain distinct. Original generic values, return objects, reentrant notifications and exceptions are preserved; diagnostics does not replay coercion or read a target getter to manufacture state.

Values remain private by default. `AVTRACE_PROPERTY_SCALARS=1` independently permits Boolean/integer/float/double payloads; strings and arbitrary structs remain redacted. `Workspace: Properties` and `Show Property history` expose searchable histories, resizable panels, selected-call/identity/source navigation and a **discrete notification chart**, not interpolated state. See [property-history contracts and validation](docs/property-history.md).

### Native viewer and sidecar development increment

Native `.dvntrace` files now open in the shared desktop/browser model, retain typed provenance on export, and have a Rider **Native** coverage preset. `NativeCapture convert` creates a standard recording; `NativeCapture relay` follows a native file over authenticated TCP/TLS with bounded, content-checked reconnect replay. The collector retains native metadata for late viewers independently from its event tail. [Usage, measurement contracts and remaining capabilities](docs/native-viewer-sidecar.md).

## Workbench and recorded semantics

The actual **Dock Rider Dark compact** theme is scoped to diagnostics and detached documents, without forcing an embedding application into dark mode. Shared tabbing, splitting, pinning, managed/native floating windows and persistent workspace topology support Inspect, Profile, Compare, Investigate, Resources, Styling, Properties and Native presets. Existing layouts survive added documents. Invalid, oversized or duplicate layouts are rejected before live mutation; settings contain panel identities/geometry, not trace data or tokens.

Synchronized subsystem timelines/thread flame charts, causal chains, latency statistics, top-down/bottom-up calling contexts, thread span coverage, baseline comparison, independent visual/logical trees, property/precedence/resource inspection, layout scenes, checkpoint differences, bookmarks, capture health and queryable event logs share selection and navigation history.

| Investigation | Recorded evidence | Contract |
| --- | --- | --- |
| Properties | Setter attempts, actual notifications, priority, direct/attached/inherited flags and optional scalar history | [Properties](docs/property-history.md) |
| Bindings | Attachment/start/stop/disposal, node/converter/source writes, publication versus sink delivery, dynamic-resource callbacks | [Bindings](docs/binding-lifecycles.md) |
| Styling | Selector results, attachment attempts, frame activity/hasChanged and activation signals | [Styling](docs/styling-evaluations.md) |
| Resources | Executed provider delegation, key/theme identity, found/null/miss/throw | [Resources](docs/resource-lookups.md) |
| Routed events | Direct/tunnel/bubble steps, handler boundaries, handled state and skipped candidates | [Routing](docs/routed-events.md) |
| XAML / dispatcher | Original compiler source points, queue admission/priority/callback/abort outcomes | [XAML and dispatcher](docs/xaml-dispatcher.md) |
| Async | Standard Task/ValueTask/async-void state-machine lifetimes, independent causal context | [Async](docs/async-lifetimes.md) |
| Sources | Original assembly/MVID/token, portable-PDB location/checksum and explicit local source reading | [Sources](docs/source-causality.md) |
| Transport | Stateless event batches and negotiated baseline-hashed reliable snapshot deltas | [Protocol](docs/STATEFUL-PROTOCOL.md) |
| Retention | Exact arrival-bounded history, chronological merge, durable gaps and benchmarks | [Retention](docs/retained-capture.md) |

A hook match does not prove execution: inspect the weaving manifest and workload capture together. Resource candidates, conditional selectors, active frames and setter returns are not automatically winning effective values. Snapshots are sampled, weakly consistent observations; truncated absence remains unknown. Managed spans include waits and are **not CPU utilization or GPU time**.

Drag the global overview to select a half-open analysis interval. Self-time subtracts the union of same-thread nested intervals, not asynchronous continuation time. Table and chart limits disclose truncation. Source files are opened only after explicit selection and checked against available PDB checksums; trace paths/URLs never trigger automatic reads or requests. Source content is not embedded in exports.

```text
subsystem:Layout duration>=1ms
(name:Measure or name:Arrange) not thread=8
object=12 duration>500us
```

`Ctrl+Shift+P` opens commands; `Ctrl+O` opens recordings, `Ctrl+S` exports, `Ctrl+L` connects, `F12` opens Sources, `F8` pauses/resumes capture, `Ctrl+B` bookmarks, `Alt+Left/Right` navigates selection history, and `Ctrl+Shift+F` toggles focus mode. Binary and Chrome Trace exports preserve recorded identities and loss provenance.

## Projects and dependencies

Ten reusable NuGet packages, under the `DiagnosticsVisualizer` prefix: **Core, Protocol, Runtime, Analysis, Transport, Agent.Avalonia, Charts, Controls, Workbench and Build**. The Build package embeds the Mono.Cecil Weaver. Collector, App, Desktop and Browser remain separate executable/shared application projects. Reusable libraries target .NET 8; tools/tests/desktop/browser use .NET 10.

The pinned UI lane is Avalonia **11.3.22**, `Dock.Avalonia.v11` / `Dock.Avalonia.Themes.Fluent.v11` **11.3.22**, and shared `Dock.Model.Mvvm` **12.1.0.7**. Do not substitute unsuffixed Avalonia 12 Dock UI packages into this build.

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

## Instrument an application

Run `bash eng/pack.sh` to build the local feed. Until NuGet.org publication, add the absolute `artifacts/packages` directory as a restore source. Add the reference through the application project or a diagnostic-only imported project file:

```xml
<ItemGroup>
  <PackageReference Include="DiagnosticsVisualizer.Build" Version="0.1.0-alpha.14" />
</ItemGroup>
```

Package reference alone does not enable rewriting. Keep diagnostic outputs separate:

```sh
dotnet publish YourApplication.csproj -c Release -o artifacts/instrumented \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true \
  -p:DiagnosticsDeepProfile=true
AVTRACE_ENABLED=1 AVTRACE_FILE="$PWD/session.avtrace" \
  dotnet artifacts/instrumented/YourApplication.dll
```

The file must not already exist. Bootstrap is inert without `AVTRACE_ENABLED=1`. Semantic options are separately opt-in: `AVTRACE_PROPERTY_OPERATIONS`, `AVTRACE_BINDING_OPERATIONS`, `AVTRACE_STYLE_EVALUATIONS`, `AVTRACE_RESOURCE_LOOKUPS`, `AVTRACE_ROUTED_EVENTS`, `AVTRACE_XAML_OBJECTS`, `AVTRACE_DISPATCHER_OPERATIONS`, `AVTRACE_ASYNC_OPERATIONS`. `AVTRACE_FLOW_EXECUTION_CONTEXT=1` enables automatic context propagation; `AVTRACE_PROPERTY_SCALARS=1` is an independent privacy decision. Other assemblies can be selected through `DiagnosticsIncludeAssemblies`. Keep portable PDBs beside inputs during weaving.

Signed identities require explicit diagnostic-only invalidation or `DiagnosticsSigningKey`. Do not distribute signature-invalidated outputs as production binaries. Shared runtime/package-cache paths, native/ReadyToRun images and trimmed/NativeAOT/single-file/browser target instrumentation are rejected. The browser is a **viewer**, not an IL-instrumented target. Clean or restore old output before changing hook version or profile.

`avtrace.manifest.json` records original/output hashes, MVIDs, matched adapters and skip reasons. Originals are retained in `.avtrace/original`. Stop the target before using the embedded weaver's `restore --directory <output>` or MSBuild target `RestoreDiagnosticsOutput`; restore validates hashes before replacing files. See [capture profiles](docs/capture-profiles.md).

## Remote and in-process sessions

```sh
COLLECTOR=src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll
export AVTRACE_TOKEN="$(dotnet "$COLLECTOR" --new-token)"
dotnet "$COLLECTOR" --archive ./captures
```

Launch an instrumented target with the same token:

```sh
AVTRACE_ENABLED=1 AVTRACE_ENDPOINT=tcp://127.0.0.1:4318 \
AVTRACE_UDP_ENDPOINT=127.0.0.1:4319 \
dotnet artifacts/instrumented/YourApplication.dll
```

Connect the workbench to `ws://127.0.0.1:4317/trace` and enter the token; never put credentials in URLs. Omit UDP for reliable-only delivery. Establishment, control and snapshot checkpoints remain reliable. Eligible event batches switch to authenticated encrypted UDP after acknowledgement; nonces survive reliable reconnects. UDP is best effort, with loss visible in analysis. Queues and datagram reassembly are bounded.

Remote endpoints require TLS/WSS and a trusted certificate; plain TCP/WS is loopback-only. The collector checks exact browser origins, authenticates viewers/producers and disconnects slow clients. Configure `--http`, `--tcp`, `--udp`, `--origin`, `--certificate`; certificate passwords use `AVTRACE_CERT_PASSWORD`. GitHub Pages hosts the static viewer, **not the collector**. An HTTPS-hosted viewer may require WSS even for loopback according to browser policy.

For in-process embedding, install `WorkbenchStyles`, start `DiagnosticsHost`, subscribe to its bounded capture stream, and attach a shared `WorkbenchModel` through `AttachInProcessAsync`. `Inspector.AttachRoot` supports explicit roots and `Inspector.ExcludeRoot` omits diagnostics from snapshots, not its rendering cost. Use out-of-process viewing for performance investigations. See [embedding](docs/embedding.md).

## Quality gates and measurement

CI builds/tests, packs/validates all ten libraries, then consumes those packages to rewrite and run a real headless Avalonia application locally and through TCP/UDP/WebSocket/control. Its source makes no diagnostics API calls. Checks assert original behavior and actual semantic evidence, not just method-hook presence. Headless UI tests exercise real docking, restoration and inspection. Browser CI runs the published WASM app in Chromium, checking real keyboard/pointer interactions, persisted layouts, screenshots and console/network failures.

Exact allocation tests use fixed warm-up, a non-inlined boundary and a bounded test-only no-GC region with empty/positive-allocation controls. Claims are limited to those warmed intervals. **Production capture and benchmarks do not suppress GC.** Cold registration, inspection, serialization and optional AsyncLocal propagation allocate. Bounded per-thread rings, weak identities, sliced inspection, per-view retention budgets and independent fault/drop counters constrain overhead; no universal overhead percentage is claimed.

```sh
dotnet run --project tests/DiagnosticsVisualizer.Benchmarks -c Release \
  -- artifacts/performance/ingestion-report.json
bash eng/pack.sh
bash eng/integration.sh
```

The reproducible ingestion benchmark validates output checksums and retained/eviction counts. It measures viewer ingestion/publication, not end-to-end UI latency or target overhead. Workflow results, not the presence of tests, determine which gates passed.

## Remaining boundaries and publishing

GPU timestamp/counter backends, complete custom-scheduler causality, exhaustive private semantic coverage and complete XAML setter/element mappings remain separate work. The current property history observes supported mutation/notification paths; it does not reconstruct every property-store candidate or prove a global winner at every moment.

Pages deployment follows browser validation on `main`. `release.yml` builds, tests, packages and checks integration before **NuGet trusted publishing** through `NuGet/login@v1` and GitHub OIDC. Register the NuGet policy for owner `wieslawsoltes`, repository `DisgnosticsVisualizer`, workflow `release.yml`, environment `nuget`, and configure `NUGET_USER`. This workflow cannot create the account-side publisher policy. Package validation is not a claim of NuGet.org upload.

MIT licensed. Dependencies retain their respective licenses.
