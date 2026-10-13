# Native method reversion at recording stop

The alpha.15 recorder emits native format version 3. Its header and checksums have the same layout as v2; version 3 additionally defines per-method state `3` (reverted for future calls, HRESULT S_OK) and `-8` (revert failed, nonzero HRESULT). The reader rejects these states in v1/v2 rather than attributing new lifecycle behavior to older recordings. Standard binary export retains version and state; current viewers still import v1/v2.

## Shutdown sequence

At a normal duration or file-budget stop the CLR-initialized native worker disables recording and closes new IL-rewrite admission. Existing GetReJITParameters callbacks have an atomic lease. The worker waits at most five seconds for those leases, then requests reversion of selected, still-active module/method definitions with a requested/supplied body. A timeout is an explicit failure, not a successful revert. No managed thread is suspended by the recorder, and no native writer waits for a worker from inside a CLR shutdown callback.

ICorProfilerInfo4::RequestRevert has both an overall HRESULT and one result per method. Each method receives its own result. Only S_OK becomes `Reverted for future calls`; failure becomes `Revert failed`, remains visible in the method table, and contributes to the terminal error total. Runtime shutdown skips this request. An unloaded/dynamically changing module may fail and must not be reported as reverted.

The runtime contract applies to subsequent calls. An existing invocation finishes the version it entered. Native calli targets and storage remain loaded until process exit so delayed finally callbacks cannot execute unmapped memory. The profiler does not call FreeLibrary/dlclose or claim successful detachment. A reverted method can still have open-at-stop or missing event pairs; those continue to make the capture incomplete. Reversion does not imply that a method executed, restore a previously recompiled caller's inlining decisions, or establish a zero-overhead process state.

## Validation

The cross-platform native fixture keeps executing after its three-second capture window. It checks original generic/ref return, filter/rethrow/finally semantics and unchanged target-file hashes, and now requires every selected method's recorded per-method reversion result to be S_OK. Native file conversion, live/late-join relays and browser imports continue through their existing gates. Unit tests cover v1/v2/v3 compatibility, inconsistent results, unknown states, every byte corruption of a v3 reference and standard binary re-export.

Official runtime contract: https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/profiling/icorprofilerinfo4-requestrevert-method

Pause/resume, authenticated native control commands, runtime profiler detachment, GPU timestamps/counters and complete custom-scheduler/XAML/private-candidate diagnostics remain separate capabilities. This change must not be described as unloading a profiler or reverting already executing stack frames.
