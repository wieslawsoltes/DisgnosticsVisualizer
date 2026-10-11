# Stateful snapshot transport

Event batches remain independently decodable. Reliable connections negotiate `SnapshotDeltas` during authentication; a legacy peer receives the original full-frame representation. UDP never carries snapshot deltas or checkpoints.

A new encoder belongs to exactly one connection. Its first snapshot is full. Later patches bind the source session, clock, preceding snapshot sequence, SHA-256 baseline digest and reconstructed snapshot digest. Node additions, removals, field masks and order changes are explicit. Property and relation sections are replaced only when changed. A full checkpoint is emitted periodically or whenever the patch would not save at least ten percent.

Queues retain full immutable wire frames. Compression occurs after dequeue, preventing backpressure or dropped event batches from corrupting a later snapshot baseline. Each collector viewer has its own encoder, so late joiners and reconnects receive an independent checkpoint chain. The collector expands producer deltas before history and viewer fan-out. Archives use their own encoder and are read by `TraceFile` through `TraceStreamDecoder`.

Decoder state is published only after complete length, identity, geometry, ordering and digest validation. Missing baselines and malformed patches are rejected; readers must reconnect or explicitly reset to a full checkpoint rather than manufacture missing state. Incomplete archive tails may be recovered at a complete-frame boundary; corrupted complete frames are not silently ignored.

Snapshots are bounded, weakly consistent observations, not transactional application states. A removal from a truncated snapshot is not proof that an object was destroyed. Snapshot history and the difference inspector preserve this distinction.
