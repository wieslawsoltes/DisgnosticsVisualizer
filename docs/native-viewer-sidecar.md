# Native recorder, shared viewer and authenticated sidecar

This development increment completes native file import, shared Rider inspection, conversion and a standalone reliable relay for PR #12. It does not add GPU timestamps or silently treat the method profiler as an Avalonia semantic agent.

## Workflows

Build the native profiler as documented in `native/README.md`, then build `tools/NativeCapture`. The caller must own or be authorized to diagnose the target process. Attachment remains explicit; opening a recording never attaches or executes code.

```sh
# Existing, already-running process; output must not exist.
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll attach \
  12345 /absolute/path/libDiagnosticsVisualizerProfiler.so \
  /absolute/path/session.dvntrace 10000 'MyApp!MyApp.MainWindow::Refresh'

# Convert a finalized recording without overwriting an existing file.
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll convert \
  /absolute/path/session.dvntrace /absolute/path/session.avtrace

# In another terminal, follow the file while its native writer is still running.
# AVTRACE_TOKEN must match the collector; do not put it in arguments or a URL.
export AVTRACE_TOKEN='<your separately provisioned capture token>'
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll relay \
  /absolute/path/session.dvntrace tcp://127.0.0.1:4318 \
  --follow --attempts 3 --idle-ms 30000
```

For a finalized file, omit `--follow`. Plain TCP is loopback-only; remote connections require `tls://host:port`, a trusted server certificate and the existing authenticated producer handshake. Browser viewers connect to the collector using WS/WSS, not directly to the native library. Follow starts after the capture file exists; a missing path fails rather than waiting indefinitely. The attach process and relay can be operated independently.

`inspect <file>` and `convert <file> <new-output> --recover` remain supported. Recovery exports explicitly incomplete evidence and never reports a missing footer as success. The previous positional relay arguments are also accepted for compatibility; the named-option path retains one file handle throughout reconnects.

Ctrl+C cancels the sidecar's I/O and returns exit code 130. It does **not** stop, unload or revert the profiler inside the target. Ping responds with PONG. Native pause/resume and snapshot requests return explicit unsupported capability responses. They are never presented as successful target control. The target recorder still uses its configured duration and stop/budget rules.

## Reconnection and integrity

The relay retains the original file handle and requires a seekable source at position zero. At most ten connection attempts are allowed (default three). Connection failures replay from the beginning with stable source session/sequence identities; `TraceStore` deduplicates repeated batches. This is at-least-once transport replay, not a claim of application-level delivery acknowledgements.

Each previously decoded trace frame has a SHA-256 replay fingerprint. A changed replayed prefix, observed file shrink, malformed framing, authentication failure, exhausted replay budget, or idle timeout fails explicitly. Authentication and invalid data are not retried. The default fingerprint budget is 131,072 frames; no unbounded pending-send queue is created. A send is awaited before decoding the next frame. Path replacement does not switch the open handle. Capture files must have an append-only prefix while being followed.

Native follow waits through temporary EOF until a valid terminal frame; inactivity is an error, never proof of completion. Version 2 headers and all frames are checksummed. Version 1 is explicitly recognized as the older unchecked-header format, not guessed from a frame prefix. Offline recovery retains incomplete evidence. Conversion requires a complete footer and commits its new output using a temporary file and a non-overwriting rename; Unix temporary files are owner-readable/writable only.

The collector separately retains bounded native method/thread/process/status metadata (8 MiB) for late viewers. It sends original metadata-only frames, not invented source sequences or edited event-bearing frames. Event history remains a bounded tail. Thus a late viewer can identify all recorded method definitions and OS threads while reporting unmatched/lost older events. Complete provenance is not complete event history. Slow viewers remain subject to the existing bounded connection queues.

## Shared desktop/browser inspection

Open either `.dvntrace` or `.avtrace` and select **Workspace: Native** or **Show Native CLR**. The Native preset omits empty visual/logical-tree panes and starts with method coverage, event details and hot paths. Those panels remain available on demand; existing preset numeric identities and saved layouts are preserved.

The shared inspector distinguishes method discovery, ReJIT request, IL submission, successful compilations and executed paired spans. An IL submission does not prove execution. Exceptional unwinds, unmatched starts/completions and invalid groups are shown separately. Native recorder slots are labelled independently from OS thread IDs. Method names, original MVIDs and MethodDef tokens survive export, including selected definitions that never executed. No Avalonia objects, source text, property values, PDB locations or GPU durations are invented by this backend.

Native metadata is validated and copied into read-only published views. Out-of-order status frames cannot roll back newer status; process/thread identities cannot change. Binary re-export retains native provenance and inherited loss information. Chrome export names native threads explicitly and includes return/unwind outcomes. The first accepted block's UTC anchor is preserved; legacy block producers may timestamp later blocks independently. This is not cross-machine clock synchronization.

## Validation

`NativeCaptureTests` and `NativeBootstrapTests` cover strict corruption/truncation handling, export, unknown terminal integrity, identity conflicts, out-of-order publication, follow stalls, bounded replay, changed replay prefixes, real TCP authentication and late-viewer metadata retention. `NativeWorkspaceTests` checks import, selection and docking, and can also render a real reference capture when `AVTRACE_NATIVE_UI_RECORDING` is set.

`native/relay_smoke.py <profiler-library>` launches an unmodified, four-thread CLR fixture, attaches the real profiler, follows its file through the actual collector and a WebSocket viewer, checks capability replies, and compares live/offline event and provenance SHA-256 fingerprints, not only counts. A late viewer must retain all eleven definitions/four thread identities while acknowledging its shortened event tail. `native/smoke.py` remains the separate native IL/behavior/hash validation.

Remaining work includes native target remote control/detach/revert, GPU backends, broader semantic/custom-scheduler coverage and exhaustive XAML/property-candidate maps. All new code must pass the hosted matrix and browser tests before this development revision is merged or published. NuGet trusted-publisher policy and release authorization remain external prerequisites; package building is not NuGet.org publication.

## Concurrent branch reconciliation

This source incorporates the later PR revision `3e65537672e721d22ed87534f85dc9dcdf721d13`. Its native demo factory, inspection/recovery CLI, reopen-source relay API, metadata-cache API, segmented-append validation and regression tests are retained. The default collector uses original-frame bootstrap sequences; it does not synthesize sequence numbers into the event stream. Reopened-source relay also validates bounded content fingerprints, not just the unchanged capture header. Native pairing uses compact event-array offsets and independently checks retained record counts against the terminal metadata.
