# Diagnostics Visualizer

A cross-subsystem Avalonia trace recorder, inspector and Fluent docking workbench. Desktop and WebAssembly share controls, charts, analysis and session models. Target instrumentation rewrites **application output copies**, not Avalonia source or the NuGet cache.

[Browser workbench](https://wieslawsoltes.github.io/DisgnosticsVisualizer/) · [Build/integration](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/build.yml) · [Browser verification/deployment](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/browser.yml)

**0.1.0-alpha.2.** An actively validated implementation, not a claim of complete coverage of every Avalonia release or Chrome DevTools feature. The browser starts with a labelled synthetic example; it never pretends to be attached to an application. Check the deployment workflow for the current publication status.

## Workbench

Zoomable subsystem timeline, thread flame chart, selected-event causal graph, subsystem map, baseline cost comparison, independent visual/logical trees, property/precedence/resource inspection, layout bounds, event details, filterable event log, hot paths and evidence-labelled findings. Panels use Dock's tabbing, splitting, pinning and floating infrastructure; browser floating windows are managed within the page. Selection is shared between trees, events and charts.

Open `.avtrace`, recover complete batches from a truncated final write, export retained data or Chrome Trace JSON, compare workload traces, or connect to a collector. `Ctrl+Shift+P` opens commands; `Ctrl+O` opens, `Ctrl+S` saves, `Ctrl+L` connects, and `F8` pauses/resumes a connected target. Drag timelines to pan, Ctrl+wheel to zoom, and double-click to fit.

## Projects

| Project | Responsibility | NuGet |
|---|---|---|
| Core | Events, metadata, snapshots and limits | Yes |
| Protocol | Bounded framing, delta/varint events, CRC, trace files, encrypted datagrams | Yes |
| Runtime | Injection ABI, per-thread buffers, weak identities, causal context and suppression | Yes |
| Analysis | Ingestion, interval queries, self-time, causality, findings and comparisons | Yes |
| Transport | Authenticated TCP/TLS/WebSocket, UDP sender and bounded queues | Yes |
| Agent.Avalonia | Target capture host, sliced inspection and weak notification observation | Yes |
| Charts | Timeline/flame, causality, subsystem and cost charts | Yes |
| Controls | Virtualized trees, properties, layout box and event details | Yes |
| Workbench | Reusable dockable workspace, shared model and styling | Yes |
| Build | Opt-in MSBuild integration and embedded weaver | Yes |
| Weaver | Mono.Cecil rewrite, manifest and checksum-checked restore | Embedded |
| Collector | Native target gateway, browser relay and optional archive | Executable |
| App / Desktop / Browser | Shared application and thin platform launchers | Applications |

Reusable libraries target .NET 8. Tools/tests/desktop use .NET 10; the browser uses .NET 10 WebAssembly. Pinned UI baseline: Avalonia **11.3.22**, Dock **11.3.11.4**. Actual rewritten assemblies and hook matches appear in the weaving manifest.

## Build and run

```sh
dotnet build DiagnosticsVisualizer.slnx -c Release
dotnet test tests/DiagnosticsVisualizer.Tests -c Release --no-build
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
  <PackageReference Include="DiagnosticsVisualizer.Build" Version="0.1.0-alpha.2" />
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

`AVTRACE_FILE` must not already exist. The injected module initializer is inert unless `AVTRACE_ENABLED=1`. Event recording starts early; tree inspection waits for Avalonia platform initialization. Framework assemblies and the entry application are candidates by default; use `DiagnosticsIncludeAssemblies` for other application libraries. Deep mode adds application control layout overrides and selected property getters.

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

Add application spans with `Probe.Enter`, instant events or annotated methods with `TraceAttribute`. `TraceContext.Capture()` creates a session-scoped parent token that can cross threads/await; `token.Resume()` installs it for a synchronous continuation. Scope/context leases are ref structs and cannot span an await. This is explicit causal propagation, not automatic interception of every Task scheduler.

## Performance and validation

Warm producer probes use bounded per-thread rings and cached metadata. A unit test checks **zero managed allocations for 10,000 warmed enabled spans**. Initial thread/object/name registration allocates: the entire agent is not allocation-free. File/network work runs on background tasks. Snapshot traversal yields between slices (default 2 ms/128 nodes), but custom getters can exceed a slice budget. Inspector work is suppressed from ordinary instrumentation and separately marked as diagnostics overhead.

CaptureOptions configures capacities, identity/metadata budgets, sampling and subsystem masks. Event loss, probe faults, metadata drops, missing batches and retained-history eviction are accounted separately. Rendering has a display budget/density fallback; causal neighborhoods are bounded. No universal overhead percentage or GPU timing claim is made. Measure representative workloads with recording disabled, balanced, deep, and with/without the viewer.

CI builds/tests, validates all ten packages, and publishes/launches an **actual rewritten headless Avalonia application consuming those packages**, with no diagnostics API calls in its source. A second execution exercises TCP establishment, encrypted UDP, WebSocket viewing and a control round trip. Reports and logs are retained. Browser CI launches the real published WebAssembly app in Chromium, checking model/visual state, keyboard commands, causal navigation, screenshots and console/network errors. Workflow results, not the presence of tests, determine which gates passed.

## Coverage boundaries

Hooks cover layout, styles/selectors, resource lookups, templates/themes, dispatcher operations, routed events, properties/bindings, XAML, notifications, both trees, rendering/composition and animation. A hook match does not prove execution: inspect the manifest together with the workload capture. Managed rendering/compositor spans measure CPU execution, **not GPU time**.

Snapshots are weakly consistent checkpoints, not a stop-the-world heap image. SnapshotDiffer keeps visual/logical/templated-parent changes separate and treats absence in truncated snapshots as unknown. The wire codec delta-encodes event fields inside independently decodable batches; snapshots are full checkpoints, **not an inter-packet tree-delta protocol**. Findings distinguish recorded relationships from inferred symptoms.

Not yet provided: arbitrary-process CLR profiler/ReJIT attachment, exhaustive asynchronous causality, XAML source-map navigation, heap leak proof, GPU counters, persistent docking layouts, or complete per-version subsystem semantic inspectors.

## Publishing

`browser.yml` tests the WebAssembly app before uploading the Pages artifact. Deployment runs outside pull requests through the `github-pages` environment and attempts Pages enablement. Where repository policy prevents enablement, configure Pages for GitHub Actions or provide an appropriately scoped `PAGES_TOKEN`.

`release.yml` builds/tests/packages and runs integration validation before publication. It uses **NuGet trusted publishing** (`NuGet/login@v1`, GitHub OIDC), not a long-lived API key. Register a NuGet policy for owner `wieslawsoltes`, repository `DisgnosticsVisualizer`, workflow `release.yml`, environment `nuget`. Set `NUGET_USER` to the NuGet profile name and create the GitHub environment. This workflow cannot create your NuGet account policy. Push a `v<version>` tag or dispatch with an exact version. See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

MIT licensed. Dependencies retain their respective licenses.
