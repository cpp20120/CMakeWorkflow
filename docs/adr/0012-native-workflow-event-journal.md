# ADR-0012 — Native workflow event journal

Status: implemented for fresh binary workflow ledgers.

Ledger epoch 5 adds structured SQLite workflow events. It keeps the epoch-4 source artifact policy and dispatch identity. Earlier binary ledgers retain their event files and can still be opened by the current Host. Older Hosts reject epoch 5. The legacy workflow and process domains continue to use their existing journals even in an epoch-5 workspace.

## Ownership and ordering

The runner owns the events it emits under the operation and run leases. The Host writes each event and its ordered artifact descriptors in a single full-synchronous SQLite transaction. The event must belong to the operation's authorized and reserved result directory. Each operation's sequence starts at 1 and advances by one. The schema prohibits updating and deleting event rows and sealed artifact rows.

The existing runner persists its checkpoint, appends the event, and then passes through the Host's checkpoint binding before it can launch a worker. An append failure stops that progression. A termination between the checkpoint and event transactions can leave progress evidence behind the checkpoint. Resume claims stay in the operation ledger and consume the existing checkpoint binding and explicit reconciliation. A progress event remains distinct from authorization, proof that an external process ran, or terminal finalization.

Resume continues from the last committed sequence. The Host's existing event RPC projects both layouts with the existing exclusive cursor and ordered page contract. The SQLite layout needs no per-run JSONL file. Legacy file replay keeps its tolerance for an interrupted final line.

Terminal workflow and process reports, recovery checkpoints and reconciliation evidence still use their existing contracts. History continues to import the verified terminal report. The native progress stream neither changes historical report hashes nor advances History independently.
