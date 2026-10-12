# Property operations and notification history

Alpha.13 adds opt-in observation of the original property mutation path. It does not change Avalonia sources, modify the NuGet cache, replay a setter/coercer, or poll a property getter to infer a value.

## Enable capture

Clean or restore previously instrumented outputs before upgrading the Build package (hook version 10). Instrument output copies with the existing MSBuild workflow. Then launch with:

```sh
AVTRACE_ENABLED=1 \
AVTRACE_PROPERTY_OPERATIONS=1 \
AVTRACE_FILE="$PWD/properties.avtrace" \
dotnet artifacts/instrumented/YourApplication.dll
```

Values are private by default. Add `AVTRACE_PROPERTY_SCALARS=1` only when the recorded application permits exporting numeric/Boolean values. This flag is independent of property-operation admission. Strings, chars, enums, nullable values and arbitrary structs remain redacted. Reference payloads carry weak object identities, not heap contents. Null, unknown/unavailable and redacted are distinct. The default redaction of property values does not remove method/property/type names or override the existing inspector's privacy settings.

## What is observed

The adapter covers supported generic and untyped SetValue, SetCurrentValue, ClearValue, CoerceValue and direct SetAndRaise entry/exit, both original RaisePropertyChanged overloads (including optimized reused generic arguments), actual EffectiveValue<T>.SetAndRaise value-entry identity, and PropertySetterInstance<T> lifecycle calls. The weaving manifest lists exact matches and unsupported shapes.

A setter call records its requested input and whether the call returned or threw. A direct SetAndRaise records its original changed/unchanged return. Neither a successful return nor a setter-instance activation is proof that its configured value won precedence. The nested original notification records the old Optional<T>, new BindingValue<T>, binding priority, direct/attached/inherits flags and effective-versus-base change flag at notification entry. Reentrant observers may modify state again before the outer notification returns; its captured values do not become the final state.

Coercion is never replayed. A request of 40 may cause an effective notification of 10. An animation-priority value can remain effective while a local base value changes underneath it. Inherited notifications use the original optimized argument path. Absent values and unassigned outputs remain unknown, rather than zero/null. No target getter is called to manufacture an old value for a setter attempt.

Known framework getters must be inert expected backing-field reads; priorities and optional/binding representations are checked before semantic rewriting. Small injected readers live inside the original Optional<T>/BindingValue<T> types, use their validated presence flags and fields, and pass typed values to generic capture without boxing. Changed priority enums or storage layouts cause semantic-hook rejection. The original typed return, subscription IDisposable identity, exception and null-argument behavior remain the application's.

## Wire and analysis

Each complete operation uses four descriptor links and one duration span. The duration span owns the original operation identity; descriptor links carry target/property, actual participant/priority, old value and new/requested value. Signed/unsigned and IEEE floating-point bit patterns are preserved. Chrome JSON encodes full-width scalar bits as strings, so JavaScript's numeric precision cannot silently alter them.

Descriptors pair independently of delivery order. Conflicting, malformed, missing, cyclic and impossible nesting evidence is marked, not used to synthesize complete durations. Synchronous property parentage is distinct from the general causal parent. Per-target/property history and child indexes are built once per analysis; selections use these indexes, not a full-recording scan.

A complete local group does not prove a complete capture. Loss, missing batches and viewer eviction remain visible. History is bounded by the viewer's retained events and by an 8,192-row document limit. This is not a durable audit log or an exact replayable property store.

## Shared Rider workspace

`Workspace: Properties` opens property history, bindings, styling, resources and sources using the existing diagnostics-scoped Rider Dark palette. It allocates more space to documents and less to findings without changing existing saved layouts. `Show Property history` selects chronological history for the selected target/property.

The request/detail and chart/table splits are independently resizable. Target, property-identity, original value-entry/setter, source and exact-call-range navigation are explicit. Setter inputs are labelled requested/configured; notification values are labelled notified.

The chart draws at most 512 **discrete, finite scalar notifications**, with solid effective-value points and outlined base-value points. It never draws setter requests as effective values, joins points into invented state intervals, or uses incomplete groups. Null, redacted, unknown and non-finite values remain in tables but do not create plotted numeric observations. Double conversion is used only for plotting; the stored bit pattern and textual integer value remain exact.

## Overhead and verification

Both features are opt-in. Generic primitive capture uses JIT-specialized type branches rather than boxing; object APIs inspect only primitives that the application already boxed. Arbitrary formatters and equality methods are not invoked. Cold identity/name registration allocates; enabled operations add bounded event writes; optional ExecutionContext propagation has its own AsyncLocal cost. Production capture does not suppress GC.

Tests cover strict warmed allocation intervals, privacy/opaque values, special numeric bits, binary export, reordered/conflicting/missing groups, pause/suppression/session replacement, changed framework shapes, and actual rewritten generic framework execution. The packaged sample independently verifies coercion, reentrant 5→6 notifications, inherited changes, attached values, direct change/no-op, animation/base separation and the original subscriber exception locally and through the collector. Browser CI activates the real shared document through keyboard input and restores its docking layout after reload.

The all-adapters reference workload uses a 262,144-event per-thread budget to accommodate startup bursts. That is a test profile, not a production default or universal zero-loss guarantee. Workflow outcomes, not the existence of these tests, determine which checks passed.

## Remaining boundaries

This does not identify every property-store candidate or prove a global winning setter at all times. Custom property pipelines, unobserved setter paths, exhaustive XAML setter mappings, native CLR attachment, GPU timings and arbitrary-scheduler causality are separate capabilities. Unsupported framework shapes are not claimed as semantic coverage. NuGet packages are built/validated; NuGet.org trusted publication still requires the account policy and repository configuration.
