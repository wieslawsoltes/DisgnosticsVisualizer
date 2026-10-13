# GPU timestamps: alpha.17

`AVTRACE_GPU_TIMINGS=1` enables an opt-in desktop OpenGL timestamp adapter on supported output-copy instrumentation. No Avalonia sources or package-cache binaries are changed. The initial lane validates Avalonia.Skia 11.3.22's complete GL session shape before editing; unsupported versions/shapes are explicitly skipped. Restore or clean previously instrumented output before updating the hook version.

## What is measured

The first device timestamp is issued after the original Skia GL rendering session is constructed, with its original context already current. The second is issued after the original canvas/context flushes and before the original platform session's disposal/presentation. The difference measures elapsed **device-clock time between those timestamps**, including intervening commands and possible idle time. It is not active GPU utilization, a hardware performance counter, or presentation latency. The adapter does not add a flush or finish.

CPU submission admission, the end-query issue, and asynchronous readback observations use the capture's CPU monotonic clock. Device counters remain separate unsigned 64-bit nanoseconds. No CPU/device offset is invented, and different GL contexts are not assumed calibrated. Charts compare available durations only. “Analyze CPU submission” selects the CPU admission-to-issue interval, never the GPU timestamps. Chrome export keeps device counters and exact intervals as decimal strings in `gpuQueries` to avoid JavaScript integer precision loss, rather than placing them at made-up CPU positions.

## Bounded asynchronous execution

`IGpuTimestampBackend` and `GpuTimestampArena` are reusable Runtime APIs. An arena owns at most 32 query pairs by default (configurable 1–256), polls at most eight slots per frame, and retires unresolved work after a bounded number of eligible polls. Results are read only after query availability. A pending limit closes that arena rather than recycling in-flight query names. A missing final rendering opportunity is not success: disposal records ContextDisposed for unresolved work.

The desktop adapter requires GL 3.3+ with 64-bit timestamp queries. Unsupported counter widths remain unsupported; overflow and reversed counters do not become negative or fabricated durations. When an application has GL_QUERY_BUFFER bound, polling is deferred: GL output pointers would otherwise be interpreted as buffer offsets. Diagnostics does not modify that binding, consume GL errors, call glFinish, spin for GPU readiness or synchronously retrieve an unavailable result. Native driver calls themselves have implementation-dependent overhead.

Query registration and descriptor discovery are cold allocation paths. Warm arena/probe tests exercise strict zero-byte intervals with the existing test-only GC-isolated harness; this is not a whole-agent or driver allocation guarantee. Optional ExecutionContext propagation has separate costs. State is weakly associated with at most 64 live contexts, and rendering is thread-affine. Context migration between producer threads is not instrumented by this lane. Known context disposal performs bounded query cleanup under a temporary EnsureCurrent lease that restores the caller's context; lost or inaccessible contexts are abandoned without GL calls. Context destruction reclaims their bounded query objects.

Sampling applies at query admission. An admitted query can finish after recording pauses, but it cannot write into a replacement capture session. Generation-checked submission tokens prevent copied/stale tokens from closing a reused slot. Constructor/driver faults, aborted submissions, context loss, capacity limits and unavailable evidence have distinct recorded outcomes. All injected ABI entry points contain managed diagnostics exceptions; application exceptions and original platform disposal remain untouched.

## Viewer and validation

Use **Show GPU timings**, **Workspace: Gpu**, or the clearly labelled **Open synthetic GPU example** command. The reusable duration chart supports keyboard and pointer selection; the table filters operation, renderer and outcome, and the chart/table split is resizable. The Native and GPU presets omit initially empty tree columns while retaining those tools on demand. Layout persistence stores no GPU descriptors or captured values.

`eng/gpu-integration.sh` consumes all locally packed NuGet packages, rewrites the actual Skia assembly and an EGL-pbuffer application, and verifies all three adapter locations. The application renders 48 real frames with no diagnostics API calls. A separate explicit backend mode verifies preserved pre-existing GL error state and an unchanged bound query buffer. The verifier requires available device intervals, valid outcomes, zero reference-workload loss/faults and exact binary re-export. Linux CI uses Mesa llvmpipe software rendering, **not physical-GPU certification**. Hardware, driver and scheduling differences are not given a universal latency threshold.

Unit tests cover asynchronous availability, capacity, generation reuse, pause/session isolation, lost contexts, exceptions, timeout, unsupported counters, partial/conflicting evidence and exports. Shared UI tests cover recorded and synthetic captures, CPU-only range semantics and dock persistence. Chromium tests exercise the published viewer with explicit synthetic data; the WebAssembly app is not an instrumented GPU target.

## Other backend and product scope

GLES/ANGLE disjoint queries, Vulkan, Direct3D and Metal backends, cross-clock calibration, hardware counters and physical-device certification are not provided by this lane. Native profiler attachment does not activate build-time Avalonia semantic hooks. Physical native profiler unload/detach, exhaustive custom-scheduler/XAML/private value-store mappings remain separate scope. NuGet packages are built/validated; actual trusted publication still requires the account-side policy and matching repository environment.

Primary semantics: Khronos OpenGL glQueryCounter and glGetQueryObject reference pages; pinned Avalonia 11.3.22 GlRenderTarget/GlSkiaGpu source.
