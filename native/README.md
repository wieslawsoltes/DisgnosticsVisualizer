# Native runtime profiling

Native attachment and method-level ReJIT capture are being implemented on this branch.
The native SDK is pinned to dotnet/runtime 60629d14374c56f1cb51819049ad1fa529307f8d
(.NET 10.0.0), under its MIT license. Public SDK archives contain headers and test
support sources only, not runner credentials or the runtime binaries.

This is separate from build-time Avalonia semantic weaving. Runtime attachment
must be explicitly requested for a process the caller is authorized to inspect.
A runtime can reject attachment; existing frames and inlined calls cannot be
claimed as newly observed merely because a ReJIT request was accepted.
