# Diagnostics Visualizer

Cross-subsystem tracing and inspection for Avalonia applications, without modifying the Avalonia source tree.

This repository is under active construction. The implementation separates instrumentation, capture, transport, analysis, reusable visualizations, and a shared Avalonia desktop/browser workbench. Compatibility and measured validation results will be documented with the implementation; this initial commit is not a production-readiness claim.

## Design constraints

- Opt-in instrumentation of application output copies, never the NuGet cache or framework source.
- Bounded recording with explicit loss accounting and no UI-thread network I/O.
- Stable weak object identities, monotonic timestamps, explicit causal relationships, and redacted values by default.
- The same binary trace format and analysis model for in-process, remote, and offline sessions.
- Separate visual and logical parent relationships; never flatten the two trees into one assumed hierarchy.
- Shared Fluent, dockable desktop and WebAssembly user interface.

Licensed under MIT.
