# Diagnostics Visualizer

A cross-subsystem Avalonia trace recorder, inspector and Rider-style docking workbench. Desktop and WebAssembly share controls, charts, analysis and session models. Target instrumentation rewrites **application output copies**, not Avalonia source or the NuGet cache.

[Browser workbench](https://wieslawsoltes.github.io/DisgnosticsVisualizer/) · [Build/integration](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/build.yml) · [Browser verification/deployment](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/browser.yml)

**0.1.0-alpha.10.** An actively validated implementation, not a claim of complete coverage of every Avalonia release or Chrome DevTools feature. The browser starts with a labelled synthetic example; it never pretends to be attached to an application. Check the deployment workflow for the current publication status.

### New in alpha.10

**Long-capture retention** now preserves exactly the latest accepted events rather than discarding entire batches. Oversized batches keep their retained tail, and retired sequence gaps remain counted. Very old replay cannot re-enter the store after the bounded sequence horizon advances.

**Incremental publication** sorts only new retained arrivals, merges them with the cached chronological run, and bulk-copies an unchanged prefix. Public event arrays remain independently owned, while unchanged read-only metadata snapshots are reused. The existing one-argument `TraceStore` constructor remains available. A new checksum-validated benchmark records ingestion/publication latency and allocations for growing and steady-state 100,000-event workloads; it is not a target-instrumentation overhead claim. See [retention, publication and benchmarks](docs/retained-capture.md).

### Previously in alpha.9

**Resource lookups** now records actual provider calls, key/theme identities, nested delegation, bool/out results and exception exits. Found nulls are distinct from misses. Returned values and deferred factories are never evaluated again by diagnostics, and opaque keys are never formatted. Enable `AVTRACE_RESOURCE_LOOKUPS=1` on instrumented output.

The shared **Resources** workspace adds searchable requests and provider calls, a bounded common-scale cascade, selected-call analysis and provider/key/returned-object navigation. The cascade/table split is resizable, actions wrap in narrow panes, and null/missing results cannot masquerade as returned-object identities. The diagnostics-scoped compact Rider palette has clearer editor/header/selection contrast; saved docking layouts remain compatible.

Packaged local and remote workloads now verify real merged/logical/theme lookup, cached deferred identity, true/null versus false outcomes, opaque-key safety and preserved construction failures. See [recorded resource resolution](docs/resource-lookups.md) for semantics, performance and measurement limits.

### Previously in alpha.8

**Routed events** now records real dispatch/handler propagation with Direct/Tunnel/Bubble phases, before/after handled state, route/handled skips and exceptional exits. It preserves typed and reflective callback exception behavior and observes aggregate class notifications without replacing callbacks. Enable `AVTRACE_ROUTED_EVENTS=1` on instrumented output; cold metadata registration allocates, while the warmed default capture path has strict zero-managed-allocation tests.

The shared route explorer provides dispatch filters, a 96-step bounded timeline, full retained-step tables, target/range navigation, and integration with Investigate. **Alt+Left / Alt+Right** navigate a transient, bounded event/object selection history. Dock's scoped Rider Dark compact palette is retained with shared table/input styles. Existing saved workspace topology remains compatible.

Binary sharing now preserves original missing-batch and viewer-eviction evidence across rebatching and repeated export; it does not invent target events or turn incomplete traces into apparently complete recordings. Chrome export includes routed fields and all integrity counters. See [routed propagation, navigation and integrity](docs/routed-events.md) for exact semantics and compatibility limits.

### Previously in alpha.7

**XAML origins** connect successful object construction and compiled root population to original portable-PDB sequence points and IL offsets. Repeated template instances retain distinct identities. Tree selection can reveal the latest retained origin in Sources; source text is chosen explicitly and checksum-verified, never reconstructed or automatically fetched from a trace. Enable `AVTRACE_XAML_OBJECTS=1` on deep-instrumented output and keep PDBs beside the input assemblies during weaving.

**Dispatcher queue** records admission, priority transitions, callback invocation, caught callback exceptions, return and abort. The common-scale chart separates inactive timer residence, runnable delay and callback time; missing markers and transport loss remain unknown. Enable `AVTRACE_DISPATCHER_OPERATIONS=1` to connect even non-ExecutionContext-flowing Post callbacks to their observed admission. The adapter checks the full supported queue shape before rewriting.

The **Investigate** docking preset brings queue timing, XAML origins, dependencies and sources together. Rider colors and dark theme variants are scoped to diagnostics and floating documents rather than changing the embedding application's requested theme. Selected document titles have an explicit contrast token; tables preserve readable column widths with horizontal scrolling. Source/dispatcher indexes are prepared with analysis rather than rescanning the trace on selection. Existing saved workspace layouts remain compatible. See [XAML and dispatcher diagnostics](docs/xaml-dispatcher.md) for semantics, budgets and tests.

### Previously in alpha.6

Standard Task/ValueTask/async-void **state-machine lifetimes** are available with `AVTRACE_ASYNC_OPERATIONS=1` on deep-instrumented output. Returned awaitables are neither wrapped nor consumed. The shared Async operations document provides outcome filtering, a bounded waterfall and exact range selection; wait time is not counted as CPU/self time.

A new **Dependencies** document connects recorded visual/logical/template parents with resource/style/template/data relationships, supports incoming/outgoing navigation, and follows historical checkpoints. Raw resource candidates include logical ancestors and merged/theme dictionaries without materializing deferred values. Snapshot-only type metadata survives transport and export. Property precedence can be filtered to the selected effective value; active frames and resource candidates are not labelled as guaranteed winners.

The Rider compact theme now highlights active panel rails. `Ctrl+Shift+F` toggles a focus workspace without destroying or persisting changes to dock topology. See [async lifetimes and semantic inspection](docs/async-lifetimes.md) for measurement boundaries, performance controls and validation.

## Workbench

Zoomable subsystem timeline, thread flame chart, selected-event causal graph, subsystem map, baseline cost comparison, independent visual/logical trees, property/precedence/resource inspection, layout scene, checkpoint differences, event details, queryable event log, calling-context trees, latency statistics, hot paths and evidence-labelled findings. Selection and timeline viewports are synchronized. Panels use Dock's tabbing, splitting, pinning and floating infrastructure; browser floating windows are managed within the page.

The actual **Rider Dark compact preset** is provided by Dock's Avalonia 11 package lane: `Dock.Avalonia.v11` and `Dock.Avalonia.Themes.Fluent.v11` **11.3.22**, shared `Dock.Model.Mvvm` **12.1.0.7**. Avalonia remains **11.3.22**. Do not mix unsuffixed Avalonia 12 UI packages into this build.

Inspect, Profile, Compare, Investigate and Resources presets are available from the toolbar and command palette. Each now opens a focused set of documents rather than every tab at once; additional tools open on demand. Existing saved layouts are preserved; reapply a preset to adopt the focused defaults. Panel activation rails, cached documents, and a single-owner view host preserve controls across docking/template changes. Workspace topology, selected tabs, proportions, pinned panels and floating window geometry are saved every two seconds and on detach. Desktop settings live under local application data; browser settings use local storage. Only whitelisted panel identities and geometry are stored, never trace content or tokens. Invalid, oversized, deeply nested or duplicate layouts are rejected before the live layout changes.

Drag the global span-activity overview to select a half-open analysis range. Statistics, top-down/bottom-up calling contexts, thread span coverage and the event log use this range. Percentiles use observed, range-clipped durations. Exclusive time subtracts the **union of same-thread nested child intervals**, not asynchronous continuation time. Span coverage includes waits inside instrumented scopes and is **not CPU utilization**. Call-tree depth/node limits are explicit. Timeline/flame charts preserve zoom during same-session refreshes.

Event queries support implicit AND, OR, NOT, parentheses, quoted names, exact 64-bit identifiers and duration units:

```text
subsystem:Layout duration>=1ms
(name:Measure or name:Arrange) not thread=8
object=12 duration>500us
```

The layout scene visualizes recorded visual-parent-relative layout bounds, not unrecorded render transforms/clipping. Snapshot history retains up to 32 checkpoints/65,536 nodes, separates visual/logical/templated-parent changes, and preserves truncated-presence uncertainty. Checkpoint labels identify batches, not atomic event-time states. Bookmarks currently live in the loaded session.

Open `.avtrace`, recover complete batches from a truncated final write, export retained data or Chrome Trace JSON, compare workload traces, or connect to a collector. `Ctrl+Shift+P` opens commands; `Ctrl+O` opens, `Ctrl+S` saves, `Ctrl+L` connects, `Ctrl+B` bookmarks, `F12` opens Sources, and `F8` pauses/resumes a connected target. Drag timelines to pan, Ctrl+wheel to zoom, and double-click to fit.

## Source-linked diagnostics

Select a recorded event and press `F12`, use **Event details → Source**, or the Sources rail icon. The shared Sources document shows the original assembly/MVID/method token, signature, portable-PDB sequence point (method entry or observed XAML site), checksum and validated SourceLink URL. Choose the matching UTF-8 source file explicitly to view a read-only, numbered excerpt; a PDB checksum mismatch is rejected. Source text and local absolute paths are not embedded in captures. No file path or URL in a trace is automatically opened or fetched. Source metadata survives binary re-export and Chrome Trace JSON export. See [source and causality](docs/source-causality.md).

## Projects

| Project | Responsibility | NuGet |
|---|---|---|
| Core | Events, metadata, snapshots and limits | Yes |
| Protocol | Bounded framing, delta/varint events, CRC, trace files, encrypted datagrams | Yes |
| Runtime | Injection ABI, per-thread buffers, weak identities, causal context and suppression | Yes |
| Analysis | Ingestion, interval queries, self-time, causality, range statistics, queries and comparisons | Yes |
| Transport | Authenticated TCP/TLS/WebSocket, UDP sender and bounded queues | Yes |
| Agent.Avalonia | Target capture host, sliced inspection and weak notification observation | Yes |
| Charts | Timeline/flame, activity, causality, subsystem and cost charts | Yes |
| Controls | Virtualized trees, properties, layout scene, view leases and event details | Yes |
| Workbench | Reusable dockable workspace, persistence, shared model and styling | Yes |
| Build | Opt-in MSBuild integration and embedded weaver | Yes |
| Weaver | Mono.Cecil rewrite, manifest and checksum-checked restore | Embedded |
| Collector | Native target gateway, browser relay and optional archive | Executable |
| App / Desktop / Browser | Shared application and thin platform launchers | Applications |

Reusable libraries target .NET 8. Tools/tests/desktop use .NET 10; the browser uses .NET 10 WebAssembly. Actual rewritten assemblies and hook matches appear in the weaving manifest.

## Build and run

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

Browser staging preserves SDK fingerprinted assets and imports the actual published runtime loader. It does not depend on an assumed SDK output layout.

## Instrument without application source changes

Build the local feed with `bash eng/pack.sh`. Add this reference to the application project or a diagnostic-only imported project file:

```xml
<ItemGroup>
  <PackageReference Include="DiagnosticsVisualizer.Build" Version="0.1.0-alpha.10" />
</ItemGroup>
<PropertyGroup>
  <DiagnosticsEnabled>true</DiagnosticsEnabled>
  <DiagnosticsAllowSignedRewrite>true</DiagnosticsAllowSignedRewrite>
  <DiagnosticsDeepProfile>false</DiagnosticsDeepProfile>
</PropertyGroup>
```

Until publication to NuGet.org, restore with the absolute `artifacts/packages` directory as an additional source. Referencing the package alone does **not** enable instrumentation. Keep diagnostic outputs separate:

```sh
dotnet publish YourApplication.csproj -c Release -o artifacts/instrumented \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true
AVTRACE_ENABLED=1 AVTRACE_FILE="$PWD/session.avtrace" \
  dotnet artifacts/instrumented/YourApplication.dll
```

`AVTRACE_FILE` must not already exist. The injected module initializer is inert unless `AVTRACE_ENABLED=1`. Event recording starts early; tree inspection waits for Avalonia platform initialization. Framework assemblies and the entry application are candidates by default; use `DiagnosticsIncludeAssemblies` for other application libraries. Deep mode adds application control layout overrides, selected property getters and application async kickoff methods.

Signed identities require explicit diagnostic-only invalidation or `DiagnosticsSigningKey`. Never distribute signature-invalidated outputs as production binaries. Package-cache/shared-runtime paths and native/ReadyToRun images are rejected. MSBuild integration rejects trimmed, NativeAOT, ReadyToRun, single-file and browser target instrumentation. The browser is a **viewer**, not an IL-instrumented target.

`avtrace.manifest.json` records hashes/MVIDs, actual matches, skipped-method reasons, notification probes and bootstrap status. Originals are in `.avtrace/original`. Stop the target before restoring; use the embedded weaver's `restore --directory <output>` or MSBuild target `RestoreDiagnosticsOutput`. Restoration verifies hashes and replaces files instead of truncating potentially mapped DLLs. Clean/restore before changing the weaving profile or hook version.

## Live TCP, UDP and browser viewing

After building, generate a random token and launch the collector. Credentials stay in environment variables, never URLs:

```sh
COLLECTOR=src/DiagnosticsVisualizer.Collector/bin/Release/net10.0/DiagnosticsVisualizer.Collector.dll
export AVTRACE_TOKEN="$(dotnet "$COLLECTOR" --new-token)"
dotnet "$COLLECTOR" --archive ./captures
```

Start an instrumented target in another terminal with the same `AVTRACE_TOKEN`:

```sh
AVTRACE_ENABLED=1 AVTRACE_ENDPOINT=tcp://127.0.0.1:4318 \
AVTRACE_UDP_ENDPOINT=127.0.0.1:4319 \
dotnet artifacts/instrumented/YourApplication.dll
```

Connect the workbench to `ws://127.0.0.1:4317/trace` and enter the token. Omit `AVTRACE_UDP_ENDPOINT` for fully reliable delivery. Initial establishment, control, snapshots and oversized batches remain on the reliable channel. Event-only batches switch to encrypted UDP after the collector acknowledges the authenticated source session. Nonce sequences survive reliable-channel reconnects. UDP is best effort: sent is not delivered, and missing batches remain visible in analysis.

Remote endpoints require TLS/WSS and a trusted certificate; plain TCP/WS is restricted to loopback. The collector checks exact browser origins, authenticates viewers/producers, bounds queues/reassembly, limits commands and disconnects slow viewers. One source session is accepted at a time. Configure `--http`, `--tcp`, `--udp`, `--origin`, `--certificate`; PFX passwords use `AVTRACE_CERT_PASSWORD`. GitHub Pages hosts the static viewer, **not the collector**. An HTTPS-hosted viewer may require WSS even for loopback, depending on browser policy.

## In-process integration

Reference Agent.Avalonia and Workbench, install `WorkbenchStyles` in an initialized Avalonia application, and keep the leases alive while the view is mounted:

```csharp
await using var host = DiagnosticsHost.Start(new AgentOptions());
await using var subscription = host.Subscribe();
await using var model = new WorkbenchModel(loadDemo: false);
await model.AttachInProcessAsync(subscription.ReadAllAsync(),
    command => Task.FromResult(host.Execute(command)));
using var view = new WorkbenchView(model);
// Mount view in a Window or another host on the UI thread.
```

`Inspector.AttachRoot` supports explicit roots; `Inspector.ExcludeRoot` omits the workbench root from snapshots. It does not remove rendering cost. Prefer an out-of-process viewer for target performance investigations. See [embedding](docs/embedding.md).

Add application spans with `Probe.Enter`, instant events or annotated methods with `TraceAttribute`. `TraceContext.Capture()` creates a session-scoped parent token that can cross threads/await; `token.Resume()` installs it for a synchronous continuation. Scope/context leases are ref structs and cannot span an await. For automatic propagation through flowing .NET execution contexts, opt in with `AVTRACE_FLOW_EXECUTION_CONTEXT=1` or `CaptureOptions.FlowExecutionContext = true`. This covers ordinary awaits and Task.Run without modifying application source, including ConfigureAwait(false). Suppressed/unsafe execution-context boundaries still require explicit tokens. Async kickoff span duration ends at synchronous return, not task completion. Separately, `AVTRACE_ASYNC_OPERATIONS=1` records standard state-machine lifetimes through their terminal transition.

## Performance and validation

Warm producer probes use bounded per-thread rings and cached metadata. A unit test checks **zero managed allocations for 10,000 warmed enabled spans in the default mode**. Optional execution-context propagation adds AsyncLocal allocations per recorded span and is disabled by default. Initial thread/object/name registration allocates: the entire agent is not allocation-free. File/network work runs on background tasks. Snapshot traversal yields between slices (default 2 ms/128 nodes), but custom getters can exceed a slice budget. Inspector work is suppressed from ordinary instrumentation and separately marked as diagnostics overhead.

CaptureOptions configures capacities, identity/metadata budgets, sampling and subsystem masks. Event loss, probe faults, metadata drops, missing batches and retained-history eviction are accounted separately. Rendering has a display budget/density fallback; causal neighborhoods are bounded. No universal overhead percentage or GPU timing claim is made. Measure representative workloads with recording disabled, balanced, deep, and with/without the viewer.

CI builds/tests, validates all ten packages, and publishes/launches an **actual rewritten headless Avalonia application consuming those packages**, with no diagnostics API calls in its source. A second execution exercises TCP establishment, encrypted UDP, WebSocket viewing and a control round trip. Reports and logs are retained. The reference workload also executes real animation and an async background notification, requiring all subsystem categories and a cross-thread notification linked to an application async kickoff. Headless UI tests exercise source checksum validation, range queries, invalid settings, view ownership, actual pinning/floating and layout restoration. Browser CI launches the actual published WebAssembly app in Chromium, checking keyboard commands, F12 source navigation, causal navigation, pointer range selection, saved-workspace reload, screenshots and console/network errors. Workflow results, not the presence of tests, determine which gates passed.

## Coverage boundaries

Hooks cover layout, styles/selectors, resource lookups, templates/themes, dispatcher operations, routed events, properties/bindings, XAML, notifications, both trees, rendering/composition and animation. A hook match does not prove execution: inspect the manifest together with the workload capture. Managed rendering/compositor spans measure elapsed managed execution (including waits), **not GPU time**.

Snapshots are weakly consistent checkpoints, not a stop-the-world heap image. SnapshotDiffer keeps visual/logical/templated-parent changes separate and treats absence in truncated snapshots as unknown. The wire codec delta-encodes event fields inside independently decodable batches; reliable connections negotiate baseline-hashed snapshot deltas with independent per-connection checkpoints and legacy full-frame fallback. UDP event batches remain stateless. See [stateful transport](docs/STATEFUL-PROTOCOL.md). Findings distinguish recorded relationships from inferred symptoms.

Not yet provided: arbitrary-process CLR profiler/ReJIT attachment, exhaustive causality through custom/unsafe schedulers, exhaustive XAML setter/element-to-IL maps beyond recorded compiler points, heap leak proof, GPU counters, or complete per-version subsystem semantic inspectors.

## Publishing

`browser.yml` tests the WebAssembly app before uploading the Pages artifact. Deployment runs outside pull requests through the `github-pages` environment and attempts Pages enablement. Where repository policy prevents enablement, configure Pages for GitHub Actions or provide an appropriately scoped `PAGES_TOKEN`.

`release.yml` builds/tests/packages and runs integration validation before publication. It uses **NuGet trusted publishing** (`NuGet/login@v1`, GitHub OIDC), not a long-lived API key. Register a NuGet policy for owner `wieslawsoltes`, repository `DisgnosticsVisualizer`, workflow `release.yml`, environment `nuget`. Set `NUGET_USER` to the NuGet profile name and create the GitHub environment. This workflow cannot create your NuGet account policy. Push a `v<version>` tag or dispatch with an exact version. See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

MIT licensed. Dependencies retain their respective licenses.
