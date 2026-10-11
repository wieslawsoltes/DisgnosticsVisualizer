# Source identities and causal execution contexts

## Record and navigate

Clean or restore previously instrumented output before applying hook version 3. Include portable PDBs beside the assemblies during weaving. Source descriptors contain the original module MVID and MethodDef token, full signature, document basename, first visible sequence point, PDB checksum and an optional validated HTTPS SourceLink URL. Async kickoff methods may obtain their location from the generated MoveNext PDB. Missing symbols retain method identity but cannot supply a source line.

Select an event, then press F12 or choose Event details → Source. Sources is a reusable shared Avalonia control and a dockable document on desktop and WebAssembly. Choose a matching UTF-8 file explicitly. The reader checks the original raw bytes against the PDB SHA256 or legacy SHA1 checksum, limits files to 2 MiB, rejects invalid UTF-8 and displays up to 40 lines on either side of the first sequence point. No checksum means an explicitly unverified excerpt, not a successful identity check.

Local absolute source paths are reduced to basenames. Source text is never serialized into captures, exported, compiled or executed. SourceLink URLs containing credentials, queries, fragments or unsupported schemes are rejected; wildcard document suffixes reject path traversal. The viewer permits copying a validated link but never fetches it automatically. This avoids trace-driven filesystem access and network requests. A checksum identifies bytes from the captured build; it does not authenticate the author of an untrusted trace.

Source descriptors use versioned symbols in the existing version-1 binary frame. Modern ingestion decodes metadata once and indexes friendly names independently of original site identity. Older readers can still decode frames but display the descriptor verbatim. Binary re-export preserves descriptors; Chrome Trace JSON exposes location, original identity and checksum in event arguments. Comparisons intentionally aggregate identical displayed signatures across modules/builds rather than failing on duplicate names.

## Automatic propagation is opt-in

```sh
AVTRACE_ENABLED=1 AVTRACE_FLOW_EXECUTION_CONTEXT=1 \
AVTRACE_FILE="$PWD/session.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

`CaptureOptions.FlowExecutionContext` defaults to false. With it enabled, a session-scoped value token flows through AsyncLocal and normal .NET ExecutionContext capture. Ordinary awaits, Task.Run and ConfigureAwait(false) retain the causal parent; ExecutionContext.SuppressFlow and unsafe/custom schedulers do not. Use TraceContext.Capture and a synchronous token.Resume lease at explicit non-flowing boundaries.

The logical parent and physical thread-local nesting stack are separate. ExecutionContext.Run on an already-instrumented thread must not borrow the unrelated caller's parent. Session identifiers prevent a continuation from a disposed capture linking into a later session. Context and scope leases are synchronous ref structs; never hold them across await. Probe errors are contained and reported.

Async kickoff methods are included in deep weaving. Their spans measure only synchronous invocation until the Task is returned, not the full asynchronous operation. Child spans after that point use continuation edges, not CPU nesting. Chrome Trace exports cross-thread/out-of-span parentage as causal flow pairs; the flow start is an anchor on the recorded parent, not an exact task enqueue timestamp. No edge is fabricated for a missing parent.

The default warmed probe path retains its zero-managed-allocation regression test. AsyncLocal mode necessarily allocates when changing ambient values and execution contexts; it is intended for targeted causal investigations. Measure disabled, balanced, deep and execution-context-enabled profiles separately. Sampling, dropped data and skipped instrumentation remain visible limitations.

## Verification and remaining boundaries

Tests cover context flow through awaits, suppressed flow, same-thread execution-context replacement, session replacement, source descriptor validation, actual IL rewrite/PDB extraction, file checksum mismatch, binary re-export, causal flow export, duplicate-signature comparisons and shared UI navigation. The packaged reference application calls no diagnostics APIs and exercises all subsystem categories, an animation, and a background INPC event linked to a source-identified async kickoff. The collector/WebSocket reference verifies the same causal relationship remotely.

This is method-level source navigation, not a debugger or complete XAML element source map. It does not provide arbitrary-process native profiler/ReJIT attachment, GPU counters/timestamps, async completion duration synthesis, or automatic causality through every custom scheduler. Private Avalonia semantics still require explicit version-specific adapters and coverage tests.
