# Native recording control (alpha.16)

This increment adds opt-in, authenticated recording control to the existing native late-attach profiler. It does not modify target assemblies on disk. The default attachment still opens **no control socket** and produces native format v3. An explicit controlled attachment uses format v4; readers continue to accept v1, v2 and v3.

## Configure the local capability

Generate an independent 32-byte random hexadecimal `AVTRACE_NATIVE_CONTROL_TOKEN`. Supply it only to the attach command and the authorized controller/sidecar. The token is not a method selector, URL parameter, recording field, or saved-workspace property. Do not reuse published example tokens. It is a local bearer capability, not a remote-network protocol or a sandbox against an administrator inspecting process memory.

```sh
# Native profiler and CLI must match the target architecture.
export AVTRACE_NATIVE_CONTROL_TOKEN="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll attach \
  <pid> <absolute-profiler-library> <new-capture.dvntrace> 60000 \
  --control-port 4320 'MyApplication!Namespace.Type::Method'
```

The attach response supplies the capture's session GUID and control port, never its capability. Binding is exclusive to IPv4 `127.0.0.1`; an occupied port causes attachment initialization to fail. No external interface is opened. No privileged port or firewall configuration is required. Clients always connect to the numeric loopback address, without DNS resolution or redirect handling.

```sh
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll control <session-guid> 4320 pause
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll control <session-guid> 4320 resume
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll control <session-guid> 4320 status
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll control <session-guid> 4320 revert
```

`stop` disables recording, acknowledges that request, then uses the existing stop-time reversion path before finalization. `revert` first disables recording and closes rewrite admission, waits up to five seconds for in-flight rewrite callbacks, then requests `ICorProfilerInfo4.RequestRevert` for the discovered eligible selected definitions and reports overall/per-method failures. `Revert accepted` is a returned CLR API result, not proof that existing frames or all compiled/inlined copies were replaced. The final footer, not a received stop acknowledgement, establishes recording finalization.

**The native library and disabled probe state remain resident.** Existing rewritten frames may still call their native addresses. No unsafe DLL unload, forced thread cancellation or profiler detach is performed. A second attachment using the same resident recorder remains unsupported.

## Live shared-workbench controls

The collector connection continues to use its independent `AVTRACE_TOKEN` and existing TCP/TLS authentication. Only an explicitly configured sidecar bridges native control:

```sh
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll relay \
  <capture.dvntrace> tcp://127.0.0.1:4318 --follow --control-port 4320
```

Remote collectors require the existing TLS policy; the native capability endpoint is local-only. The viewer requests an additive `Capabilities` command. The sidecar authenticates a **Status** request against the session read from the capture before advertising `NATIVE_PAUSE_RESUME`. Merely importing a format-v4 file, receiving recorded state metadata, or reconnecting does not grant control. Connection-generation/session checks prevent stale acknowledgements from re-enabling controls. Recording metadata updates refresh the native workbench even when no method events are arriving.

The viewer's existing Record/Pause and F8 controls then drive native pause/resume. Native snapshots, object selection and arbitrary commands remain unsupported. Stop/revert are explicit local CLI operations, not silently mapped onto the viewer's nonterminal Pause command. Canceling a relay closes its connection but does not stop the native target recorder.

## Admission and measurement contract

Pause changes admission of **new root invocation trees**. Every observed Enter still pushes a bounded thread-local nesting record until recording stops. An already admitted tree and its nested calls can finish while paused. A root admitted while paused remains suppressed across resume, including its later children. Paused work is intentional exclusion, not dropped-data accounting; independent capacity failures still count as loss. State timestamps are worker observations, not global stop-the-world barriers: concurrently executing admission probes may straddle a transition. No synthetic completion, resume-time parent or effective execution interval is manufactured.

Stop disables all probes, waits for hot callbacks to leave their bounded critical sections, drains rings and writes a footer. Application methods may still be open, so stop/revert can honestly leave unmatched entries. These remain incomplete observations. Original application return values, exceptions and finally blocks execute normally.

Control I/O runs solely on the profiler's worker thread. The hot callback path adds one atomic paused-state read only for root admission; it performs no socket/file I/O. Nonblocking control messages have fixed request/response bounds (72/64 bytes), a 250 ms I/O deadline per connection (an authorized CLR reversion request additionally has a bounded five-second rewrite-drain wait) and a four-connection listener backlog. The capability comparison accumulates differences across all 32 token bytes. Session, request nonce, operation and reserved fields are checked. Invalid credentials/session close without acknowledgement. A lost acknowledgement is **unknown**, not proof that an operation failed or rolled back. Mutations are set-state operations, never toggles.

At most 1,024 control-state revisions are retained; the final revision is reserved for stop/revert. Further pause/resume requests are rejected before mutation. A checked one-second native heartbeat keeps a legitimately paused capture from looking like an abandoned file. Heartbeats contain only source-clock time and existing state/revision, not application data or capability secrets.

## Wire and provenance

Native format v4 adds checksummed frame 6 (revision, state, timestamp, operation, result, method count, failure count) and frame 7 (timestamp, state, revision). Monotonic revisions, chronological heartbeats, impossible states, terminal conflicts and unsupported versions are rejected. The source process clock remains authoritative. State metadata is retained independently of method-event history and survives standard binary export and collector late joining. The workbench shows the latest observed control state and revert results; it does not interpolate unobserved activity.

Terminal reasons 5/6/7 distinguish user stop, accepted revert, and incomplete revert. Method statuses 3/-8 distinguish accepted/failed revert. They do not erase prior recorded execution evidence. Format-v1/v2 sources cannot declare reversion results; v3 adds reversion results. Control and heartbeat frames and user-stop reasons require v4.

## Validation

Managed tests exercise fragmented responses, identity/nonce/result rejection, cancellation versus timeouts, private capabilities, every control-frame byte mutation/truncation, invalid state sequences, export and live-workbench capability reset. Existing v1/v2/v3 checks remain enabled.

`native/control_smoke.py` launches an uninstrumented managed fixture, attaches the newly built profiler, rejects wrong capabilities/sessions and stalled clients, drives held calls across both pause boundaries, tests an authenticated WebSocket viewer/collector/sidecar round trip, and requests stop-and-revert. It checks original output-file hashes and post-revert application behavior. Native CI executes this on Windows, Linux and macOS alongside all earlier attachment/import/relay tests. A passing local platform is not a substitute for the other platforms' workflow results.
