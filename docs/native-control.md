# Native recording control

Continue from the merged alpha.14 native profiler with explicit local capability authentication, session-bound pause/resume, controlled stop and per-method RequestRevert results. The native endpoint is disabled unless explicitly requested during attach. Remote viewers use the existing authenticated collector and a separately authorized sidecar.

Pause excludes new root invocation trees, while admitted trees may finish and suppressed trees remain suppressed across resume. A pause must never drop stack bookkeeping and manufacture mismatched leave events. Recorded state and heartbeat frames distinguish intentional inactivity from a stalled writer. An acknowledgement is not proof of finalization; only the terminal integrity frame establishes that the recording ended.

Revert affects future dispatch. Existing rewritten frames can retain native callback addresses, so the recorder and native module remain resident. No unsafe DLL unload or forced thread termination is performed. Native snapshots and CLR detach remain unsupported by this increment.

Implementation is under validation. The local solution currently builds and its controlled Linux fixture verifies wrong-token/session rejection, held-call pause boundaries, authenticated WebSocket-to-native pause/resume, original application behavior and successful revert requests. Final hosted cross-platform, package and browser results must be checked before merge.
