# Retained capture publication and integrity

Alpha.10 separates arrival-bounded event retention, chronological view publication, and bounded sequence accounting. These changes run in the viewer's reusable Analysis library and are shared by desktop, browser and offline verification. Target probe code and the wire format are unchanged.

## Retention contract

`TraceStore(maxEvents)` retains up to exactly `maxEvents` unique accepted arrivals. Deduplication is against retained IDs and all IDs accepted within the current batch. When capacity is exceeded, only the required oldest arrival prefix is evicted. The retained tail of an oversized batch is preserved rather than dropping that entire batch. Retention is by arrival order, not chronological start time: late UDP arrivals and long-running completed spans can have old timestamps.

Evicted-event accounting counts only accepted records, not duplicate observations. A public event array is independently owned; mutating one publication cannot corrupt another or the private chronological cache. Event-ID replay protection is limited to retained identities; sequence replay is separately enforced as described below. The one-argument constructor remains for source and binary compatibility, with an additive two-argument overload for the sequence budget.

A large partially consumed head chunk is compacted when it retains a small tail. Identity hash storage is trimmed after oversized batches when its capacity substantially exceeds the configured event budget. Temporary acceptance work remains proportional to the incoming bounded frame, and the store retains a separate O(n) chronological cache. This is not a zero-allocation or constant-memory claim independent of the configured budget.

## Incremental chronological publication

Previously, every `GetView` flattened and fully sorted all retained events. The new index tags accepted chunks with a generation, sorts only new retained arrivals, filters evicted cached identities and merges the sorted delta with the existing chronological run. Comparison uses `(Timestamp, Id)` without subtraction overflow. An unchanged chronological prefix is located with binary search and copied in bulk.

For n retained cached events and m new retained events, publication is O(n + m log m) instead of a fresh O((n + m) log(n + m)) sort. There is still an O(n) copy for the public array. Repeated reads without new events do not re-sort or rebuild the cache. Reintroducing an already evicted ID cannot resurrect the old cached payload.

Published symbol, source-site and object metadata dictionaries are read-only snapshots. They are reused until the respective metadata changes; older snapshots do not observe later updates. Existing object metadata can still update when the identity budget is full. This change does not make the public SnapshotData arrays a new deeply immutable API; existing snapshot ownership conventions remain in force.

## Sequence horizon

The sequence ledger retains at most 1,000,000 active sequence identities by default. Its additive constructor setting accepts a smaller positive budget for constrained readers/tests. Missing gaps below a retired sequence horizon remain counted permanently instead of disappearing when old sequence entries leave the set. A late packet inside the active horizon can fill a gap. A packet at or below the retired horizon is ignored and cannot reintroduce events or change the current snapshot.

The first observed sequence may be greater than one, for example when a viewer joins an existing collector. The ledger does not invent missing history before its earliest observed range. Retired gaps are unrecoverable in that store even if an extremely delayed batch later arrives: this is the explicit tradeoff of bounded bookkeeping. They survive binary rebatching through the existing integrity metadata. Producer drops, inferred missing batches and viewer evictions remain separate counters.

All store changes and publications remain serialized by the store gate. Resource/dispatcher/routing analyzers continue to see loss evidence; retirement cannot make an incomplete recording appear complete.

## Reproducible benchmark

```sh
dotnet run --project tests/DiagnosticsVisualizer.Benchmarks -c Release -- artifacts/performance/ingestion-report.json
```

The benchmark uses a deterministic 100,000-event starting trace, twelve 256-event arrival batches, three alternating strategy trials and two modes: growing recording and steady-state bounded retention. New timestamps deliberately overlap the recent history. The reference uses arrival-bounded queue storage followed by full `OrderBy(Timestamp).ThenBy(Id)` publication. The candidate uses `TraceStore`, including its deduplication, sequence bookkeeping and independently owned output arrays.

Warm-up and initial population are outside the measured intervals. Each interval includes the new batch's ingestion and chronological publication. Output consumption/checksums are outside timing; mismatched retained counts, eviction counters or checksums fail the benchmark. The report retains all measured samples and medians, managed bytes allocated on the measured thread, runtime and architecture. Unit tests separately compare complete arrays against a simple reference model, so the checksum is not the sole correctness test.

CI records results but deliberately does not enforce a timing threshold on variable shared runners. Results measure viewer ingestion/publication only, not target probes, layout rendering, end-to-end remote latency, CPU utilization or GPU completion. Reproduce on representative hardware and workload sizes before drawing broader performance conclusions.

## Tests

Regression coverage includes exact partial-batch eviction, oversized batches, duplicate IDs, reintroduction of evicted IDs, changed input/output arrays, randomized arrivals and publication cadence, concurrent writers/readers, out-of-order timestamps and tie ordering, extreme 64-bit sequences, retired versus fillable gaps, late joins, binary integrity re-export, invalid budgets and read-only metadata publication. Existing full core/UI suites, real packaged local/remote capture and Chromium interaction gates continue to run.
