# Native CLR attachment and diagnostics

Opt-in late attachment to a same-user already running .NET process through the public DiagnosticsClient and CLR profiling interfaces. The target has no startup profiler flags or application diagnostics references. Select exact methods, request ReJIT plus affected inliners, and record native method lifetimes to a bounded binary file without modifying target assemblies on disk.

The native callback base and IL list rewriter are from the MIT-licensed .NET Foundation runtime SDK, pinned at `60629d14374c56f1cb51819049ad1fa529307f8d`. The build verifies source hashes and extends a **build-directory copy** of the sample rewriter with local-signature/outer-finally editing. The SDK checkout and Avalonia sources remain unchanged. Dependency attribution accompanies redistributed binaries.

Build with `cmake -S native -B artifacts/native-build -DRUNTIME_SDK_ROOT=<pinned SDK checkout>` then `cmake --build artifacts/native-build --config Release`. Build `tools/NativeCapture`, then invoke `NativeCapture attach <pid> <library> <new-output.dvntrace> <duration-ms> 'Assembly!Namespace.Type::Method'`. Specify 1–64 exact method names; overloads with that name are selected and distinguished by their original MethodDef tokens. Output must not exist. Paths refer to the target machine/namespace. Attach permission and diagnostics availability are runtime/OS requirements.

The added outer finally preserves original generic/byref returns and original exception/filter/finally behavior; native spans end on normal return or exceptional unwind without observing exception payloads. Target arguments and managed object identities are not captured. Inliner recompilation is requested using `RequestReJITWithInliners`; existing active invocations need not switch versions until their next call. Source identity is the original assembly/MVID/token, not an exact original sequence point. Original instruction maps are supplied to the CLR.

Capture stops after a bounded duration (100 ms to one hour). File capacity is 128 MiB, with 64 lifetime thread slots, 4,096 records per per-thread ring, 256 nested selected methods per thread, and 4,096 selected definitions. Overflows are counted, never silent. Method selection and per-method request/instrumentation/compilation results are recorded separately. A successful AttachProfiler call does not prove any selected method compiled or executed. Tail-call/jmp and unsupported/oversized signatures/bodies are explicitly rejected. The sample profile is not a universal overhead guarantee.

The profiler's writer runs on its own CLR-initialized native worker, never in a JIT callback. Hot callbacks avoid managed execution and preserve native last-error state. Native storage and the loaded library remain valid until process exit because already compiled calli sites may outlive capture. Stopping capture is **not** unloading or detaching the profiler; disabled probes retain their branch/call overhead. No other installed profiler is replaced.

Raw capture import, coverage inspection and the reliable sidecar are implemented below. Native capture currently supplies selected synchronous method lifetimes and metadata, not the managed agent's Avalonia object trees, setter values, asynchronous logical lifetime, GPU timestamps or full custom-scheduler causality. Do not interpret elapsed spans as CPU utilization.

## Shared viewer and reliable sidecar (alpha.14)

Open a `.dvntrace` directly in either application. The reader autodetects native versus
standard trace framing, validates the v2 header and every frame checksum, enforces size,
identity and record budgets, and retains a missing footer as incomplete evidence in recovery
mode. Legacy v1 files retain an explicit unchecksummed-header version marker.
Native process/method/thread/terminal provenance survives standard binary export. Native
recorder slots and OS thread IDs are distinct; the Native CLR document maps them. Open calls
at stop, missing pairs and ReJIT errors are not reclassified as successful completion.

Use **Workspace: Native** / **Show Native CLR capture** for method coverage, original
MVID/token identities, compilation counts, paired lifetimes and unwinds. Native defaults
show thread activity and event details rather than empty Avalonia object trees. The explicit
**Open synthetic native example** command supplies demonstration data; saved layouts do not
save recordings, tokens or attachment settings.

The standalone tool also supports:

```sh
# Validate and summarize raw or converted native provenance.
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll inspect session.dvntrace
# Streaming conversion; publish the new output only after input validation. Never overwrite.
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll convert session.dvntrace session.avtrace
# Add --recover explicitly to preserve a complete prefix with incomplete terminal provenance.
# Follow an existing append-only capture with an authenticated collector connection.
# AVTRACE_TOKEN must match the collector; credentials are not accepted in the URL.
dotnet tools/NativeCapture/bin/Release/net10.0/NativeCapture.dll relay session.dvntrace tcp://127.0.0.1:4318 30000 3
```

Plain TCP is restricted to loopback. Remote collectors require `tls://` with a trusted
certificate. The browser still connects through the collector's WebSocket/WSS endpoint.
The relay validates while reading, awaits each send, and does not buffer an unbounded backlog
in memory or block the target's callback threads. Physical EOF before a valid footer is
transient; idle timeout/cancellation is failure, never completion. Choose the idle budget to
allow the target's expected quiet periods.

Reconnect reopens the explicitly supplied recording from its header and replays identical
session/batch/event identities, bounded to at most ten retries. Source session and clock
cannot change on reconnect. Delivery is **at least once**, not a per-frame acknowledgement;
normal ingestion de-duplicates replay. Authentication failures, corrupt input, source stalls
and explicit cancellation are not retried. The native file remains the durable source; if it
is modified rather than appended, forwarding is not a recovery mechanism. The collector
retains up to 2 MiB of native provenance for a late viewer. It materializes metadata before
the retained history tail without inventing missing batches or target events.

Ping/control responses work over the relay. Native pause/resume/object inspection/snapshots
are explicitly unsupported and are not advertised as successful. The viewer disables its
record/pause button for native captures. Cancellation stops the sidecar, not the native
recorder: the recorder still stops at its configured duration. A new remote process attach
cannot be triggered by opening a file or sending a viewer command.

The native workflow runs real attachment on Windows, Linux and macOS, then runs the actual
reader, streaming conversion, binary re-export, TCP authentication, late-joining WebSocket
viewer and ping round-trip against that platform's recording. Late viewers may legitimately
have unmatched completions whose entries predate retained history. Tests preserve this
uncertainty; they do not require identical event counts between full and late-join captures.
