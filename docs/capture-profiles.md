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

Deep instrumentation of synchronous startup can fill a 4096-event ring before the first background drain. For a short deep startup investigation, use `AVTRACE_EVENTS_PER_THREAD=65536`; the integration workload uses this capacity and requires zero producer-side drops. More capacity consumes proportionately more memory for each observed thread. Do not extrapolate that test into a guarantee for every workload. Overflow remains bounded and reported.

For long sessions, prefer balanced instrumentation, narrow subsystem masks and sampling rather than continually increasing memory. Sampling retains valid recorded parents but can omit intermediate operations; absence is not proof that an operation never occurred. UDP loss is independent of producer overflow. Snapshots and control remain on the reliable transport.
