# Capture budgets

The agent reads these environment defaults once at construction. An explicit `AgentOptions.Capture` overrides them. No environment lookup or parsing occurs on the probe hot path.

| Variable | Default | Meaning |
|---|---:|---|
| AVTRACE_EVENTS_PER_THREAD | 4096 | Power-of-two ring capacity, 16–1048576 |
| AVTRACE_MAX_THREADS | 128 | Producer thread limit, 1–32767 |
| AVTRACE_MAX_OBJECTS | 100000 | Weak object identity budget |
| AVTRACE_MAX_SYMBOLS | 65536 | Interned site/name budget |
| AVTRACE_SAMPLE_EVERY | 1 | Record every Nth span per producer |
| AVTRACE_SUBSYSTEM_MASK | all bits | Decimal or 0x-prefixed subsystem bit mask |
| AVTRACE_FLOW_EXECUTION_CONTEXT | off | Automatic causal context flow; adds AsyncLocal overhead |
| AVTRACE_ASYNC_OPERATIONS | off | Standard state-machine lifetimes, including suspension |
| AVTRACE_DISPATCHER_OPERATIONS | off | Admission, priority and callback lifecycle observations |
| AVTRACE_ROUTED_EVENTS | off | Routed dispatch/handler/handled-state observations |
| AVTRACE_XAML_OBJECTS | off | Successful construction/root-population source observations |
| AVTRACE_RESOURCE_LOOKUPS | off | Original provider bool/out outcomes and nested search paths |

Deep instrumentation of synchronous startup can fill a 4096-event ring before the first background drain. For a short deep startup investigation, use `AVTRACE_EVENTS_PER_THREAD=65536`; the integration workload uses this capacity and requires zero producer-side drops. More capacity consumes proportionately more memory for each observed thread. Do not extrapolate that test into a guarantee for every workload. Overflow remains bounded and reported.

For long sessions, prefer balanced instrumentation, narrow subsystem masks and sampling rather than continually increasing memory. Sampling retains valid recorded parents but can omit intermediate operations; absence is not proof that an operation never occurred. UDP loss is independent of producer overflow. Snapshots and control remain on the reliable transport.

The opt-in switches above use `1` to enable. Resource lookup sampling retains whole admitted lookup trees; read [resource resolution](resource-lookups.md) before interpreting provider durations or missing calls. These features do not enable unsupported target platforms or native profiler attachment.
