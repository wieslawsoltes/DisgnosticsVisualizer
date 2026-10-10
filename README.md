# Diagnostics Visualizer

A cross-subsystem Avalonia trace recorder, inspector and Fluent docking workbench. Desktop and WebAssembly use the same controls, charts, analysis and session model. Target instrumentation rewrites **application output copies**, not Avalonia source or the NuGet cache.

[Browser workbench](https://wieslawsoltes.github.io/DisgnosticsVisualizer/) · [Build and integration checks](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/build.yml) · [Browser verification / deployment](https://github.com/wieslawsoltes/DisgnosticsVisualizer/actions/workflows/browser.yml)

**Version: 0.1.0-alpha.2.** This is an actively validated implementation, not a claim of complete coverage of every Avalonia release or Chrome DevTools feature. The browser starts with an explicitly labelled synthetic example; it never pretends to be attached to an application.

## Workbench

The workspace includes a zoomable subsystem timeline, thread flame chart, selected-event causal graph, subsystem map, baseline cost comparison, independently navigable visual and logical trees, property/precedence/resource inspection, layout bounds, event details, filterable event log, hot paths and evidence-labelled findings. Document/tool panels use Dock's tabbing, splitting, pinning and floating infrastructure; browser floating windows are managed within the page. Selection is shared between trees, events and charts.

Open `.avtrace` files, recover complete batches from a truncated final write, export retained data or Chrome Trace JSON, compare workload traces, or connect to a collector. `Ctrl+Shift+P` opens the command palette; `Ctrl+O` opens a trace, `Ctrl+S` saves, `Ctrl+L` connects, and `F8` pauses/resumes a connected target. Timeline dragging pans, Ctrl+wheel zooms, and double-click fits.

## Projects and packages

| Project | Responsibility | NuGet package |
|---|---|---|
| `Core` | Events, metadata, snapshots, limits and subsystem identities | Yes |
| `Protocol` | Bounded binary framing, delta/varint event encoding, CRC, trace files, secure datagrams | Yes |
| `Runtime` | Injection ABI, per-thread buffers, weak identities, causal context, suppression | Yes |
| `Analysis` | Bounded ingestion, interval queries, self-time, causality, findings, comparisons, snapshot differences | Yes |
| `Transport` | Authenticated TCP/TLS/WebSocket, encrypted UDP sender, bounded queues | Yes |
| `Agent.Avalonia` | Target capture host, sliced tree/property inspection and weak notification observation | Yes |
| `Charts` | Timeline/flame, causal graph, subsystem map and cost charts | Yes |
| `Controls` | Virtualized trees, property inspector, layout box and event details | Yes |
| `Workbench` | Reusable dockable workspace, session model and shared styling | Yes |
| `Build` | Opt-in MSBuild integration and embedded weaver executable | Yes |
| `Weaver` | Mono.Cecil rewrite, injection manifest and checksum-checked restore | Embedded in Build |
| `Collector` | Native target gateway, browser relay and optional archive | Executable |
| `App`, `Desktop`, `Browser` | Shared application and thin platform launchers | Applications |

Reusable libraries target .NET 8. Repository tools/tests/desktop use .NET 10; the browser targets .NET 10 WebAssembly. The pinned UI compatibility baseline is Avalonia **11.3.22**, Dock **11.3.11.4**. Hook matching is recorded against the assemblies actually rewritten.

## Build and run

```sh
dotnet build DiagnosticsVisualizer.slnx -c Release
dotnet test tests/DiagnosticsVisualizer.Tests -c Release --no-build
dotnet run --project src/DiagnosticsVisualizer.Desktop -c Release
```

Build the browser with the WebAssembly workload and serve the staged directory:

```sh
dotnet workload install wasm-tools
dotnet publish src/DiagnosticsVisualizer.Browser -c Release -o artifacts/browser-publish
python3 eng/prepare-browser.py
python3 -m http.server 8080 --directory artifacts/site
```

The staging step preserves .NET's fingerprinted runtime files and rewrites the bootstrap import to the actual published loader. It does not depend on an assumed SDK output-directory layout.

## Instrument an application without changing its source

Build the local package feed first with `bash eng/pack.sh`. Add the package reference to the application project (or a diagnostic-only imported project file):

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

Until packages are published to NuGet.org, restore using the absolute `artifacts/packages` directory as an additional source. Instrumentation is **off by default**, independent of whether the package is referenced. Use a separate diagnostic output directory:

```sh
dotnet publish YourApplication.csproj -c Release -o artifacts/instrumented \
  -p:DiagnosticsEnabled=true -p:DiagnosticsAllowSignedRewrite=true
AVTRACE_ENABLED=1 AVTRACE_FILE="$PWD/session.avtrace" \
  dotnet artifacts/instrumented/YourApplication.dll
```

`AVTRACE_FILE` must not already exist. The generated module initializer is inert unless `AVTRACE_ENABLED=1`. Bootstrap starts event capture early, while tree inspection waits for Avalonia platform initialization. Framework assemblies and the entry application are candidates by default; use `DiagnosticsIncludeAssemblies` for additional application libraries. Deep mode adds application control layout overrides and selected property getters.

Signed identities require an explicit diagnostic-only invalidation opt-in or `DiagnosticsSigningKey`. Never distribute signature-invalidated diagnostic outputs as production binaries. The weaver refuses package-cache/shared-runtime locations and native/ReadyToRun images. The build integration rejects trimmed, NativeAOT, ReadyToRun, single-file and browser target instrumentation. The browser is a **viewer**, not an IL-instrumented target.

Every rewritten output has `avtrace.manifest.json` with assembly hashes/MVIDs, actual method matches, skipped-method reasons, notification probes and bootstrap status. Originals live in `.avtrace/original`. Stop the target before restoring; use the embedded weaver's `restore --directory <output>` command or the `RestoreDiagnosticsOutput` MSBuild target. Restoration verifies hashes and uses replacement files rather than truncating potentially mapped DLLs. Clean/restore before changing the weaving profile or hook version.

## Live TCP, UDP and browser viewing

Generate a new random token, then run the collector. Keep credentials in the environment, not URLs:

```sh
export AVTRACE_TOKEN="$(dotnet run --project src/DiagnosticsVisualizer.Collector -- --new-token)"
dotnet run --project src/DiagnosticsVisualizer.Collector -- --archive ./captures
```

Start an instrumented target with the same token:

```sh
AVTRACE_ENABLED=1 AVTRACE_ENDPOINT=tcp://127.0.0.1:4318 \
AVTRACE_UDP_ENDPOINT=127.0.0.1:4319 \
dotnet artifacts/instrumented/YourApplication.dll
```

Connect the workbench to `ws://127.0.0.1:4317/trace` and enter the token. Omit `AVTRACE_UDP_ENDPOINT` to send everything reliably. With UDP enabled, initial session establishment, control responses, snapshots and oversized frames use the reliable channel. Event-only batches switch to encrypted UDP only after the collector acknowledges the authenticated session. The sender keeps its nonce sequence across reliable-channel reconnects. UDP is best effort: sent is not delivered, and missing batches remain visible in analysis.

Remote endpoints require TLS/WSS with a trusted certificate; plain TCP/WS is restricted to loopback. The collector validates exact browser origins, authenticates viewers and producers, bounds queues and datagram reassembly, limits control requests, and disconnects slow viewers rather than accumulating unbounded buffers. Only one source session is accepted at a time. Configure `--http`, `--tcp`, `--udp`, `--origin` and `--certificate`; the PFX password is read from `AVTRACE_CERT_PASSWORD`. GitHub Pages hosts static viewer assets only: it does **not** host the native collector. HTTPS-hosted viewers may require a WSS collector even for loopback, depending on browser policy.

## In-process and reusable APIs

Reference `Agent.Avalonia` and `Workbench`, install `WorkbenchStyles` in an initialized Avalonia application, and attach the local stream:

```csharp
await using var host = DiagnosticsHost.Start(new AgentOptions());
using var subscription = host.Subscribe();
var model = new WorkbenchModel(loadDemo: false);
await model.AttachInProcessAsync(subscription.ReadAllAsync(),
    command => Task.FromResult(host.Execute(command)));
var view = new WorkbenchView(model);
// Put view in a Window or another host, and dispose view/model/subscription on close.
```

`Inspector.AttachRoot` supports explicitly hosted roots; `Inspector.ExcludeRoot` omits the workbench root from tree snapshots. This does not remove the cost of rendering an in-process viewer. Prefer an out-of-process viewer when investigating target performance.

Applications can add synchronous spans with `Probe.Enter`, explicit instant events, and annotated methods with `TraceAttribute`. `TraceContext.Capture()` creates a session-scoped parent token that may cross threads/await; `token.Resume()` installs that parent for a synchronous continuation. `TraceScope` and context leases are ref structs and must not span an await. This is explicit causal propagation, not automatic interception of every Task scheduler.

## Performance and evidence

Warm producer probes use bounded per-thread rings and cached metadata. A unit test checks **zero managed allocations for 10,000 warmed enabled spans**. Initial thread/object/name registration allocates; this is not a zero-allocation claim for the entire agent. File/network work runs on background tasks. Snapshot traversal yields between slices (default 2 ms and 128 nodes), but individual custom getters and user code can exceed a slice budget. Inspector work is suppressed from ordinary instrumentation and marked by separate diagnostics spans.

`CaptureOptions` configures per-thread capacity, thread/object/symbol budgets, sampling and subsystem mask. Overflow, probe faults, metadata drops, missing batches and retained-history eviction are accounted separately. Span rendering has a display budget and density fallback; causal graph neighborhoods are capped. No universal overhead percentage or GPU timing claim is made: benchmark representative workloads with capture disabled, balanced, deep and with/without the viewer.

CI builds the solution, executes protocol/runtime/weaver tests, validates all ten packages, then publishes and launches an **actual rewritten headless Avalonia application using those packages**, with no diagnostics calls in its source. A second execution exercises TCP establishment, encrypted UDP telemetry, WebSocket viewing and a control round trip. Capture/coverage/network reports are retained as artifacts. Browser CI launches the published WebAssembly app in Chromium and checks real model/visual state, keyboard commands, the causal graph, screenshots and console/network errors.

## Current boundaries

Hook support includes layout, styles/selectors, resource lookups, templates/themes, dispatcher operations, routed events, properties/bindings, XAML, notifications, both trees, rendering/composition and animation. A hook match is not proof that a workload executed it: inspect the weaving manifest and capture report together. Rendering/compositor spans measure managed CPU execution, **not GPU execution time**.

Snapshots are weakly consistent checkpoints, not a stop-the-world heap image. Node diffing keeps visual/logical/templated-parent changes separate and treats missing nodes in truncated snapshots as unknown. The wire codec delta-encodes event fields within independently decodable batches; snapshots are full checkpoints, not an inter-packet tree-delta protocol. Findings distinguish recorded relationships from inferred symptoms. This implementation does not yet provide arbitrary-process CLR profiler/ReJIT attachment, exhaustive asynchronous causality, XAML source-map navigation, heap leak proof, GPU counters, persistent docking layouts, or full per-version subsystem semantic inspectors.

## Publishing

`browser.yml` stages and tests the actual WebAssembly app before uploading a Pages artifact. Deployment runs only outside pull requests, using the `github-pages` environment. The workflow attempts Pages enablement; a repository whose settings prohibit automatic enablement needs Pages configured for GitHub Actions or an appropriately scoped `PAGES_TOKEN`.

`release.yml` builds/tests/packages and runs integration validation before publishing. Its publish job uses **NuGet trusted publishing** (`NuGet/login@v1`, GitHub OIDC), not a stored long-lived API key. Register a NuGet trusted-publisher policy for owner `wieslawsoltes`, repository `DisgnosticsVisualizer`, workflow `release.yml`, environment `nuget`; configure `NUGET_USER` as the NuGet profile name, and create the corresponding GitHub environment. A workflow cannot create this NuGet account policy on your behalf. Push a `v<version>` tag or dispatch the workflow with the exact version. See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

MIT licensed. Third-party dependencies retain their respective licenses.
