# ADR-0013 — Native terminal workflow evidence

Fresh binary workflow ledgers use epoch 6 and an execution document catalog at epoch 4. The shared physical CAS continues to use the document-aware marker. Older collectors refuse the new catalog version before pruning. Workflow and execution plan document identities use their existing codecs and policies. Legacy workflows and binary ledger epochs 2–5 keep their original terminal report authority.

## Report and artifact identities

The generated `executionReport.capnp` schema encodes the terminal runner report, benchmark trial coordinates and samples, process resource measurements, typed profiler capture evidence, and reconciliation history. Timestamp encoding preserves local ticks and offset. Optional numeric and boolean fields use pointer presence to distinguish absent and zero values. Lists and map entries preserve their source order. Timing and resource values use their existing widths. Operator cancellation, when recorded before report publication, is included separately from canceled outcomes caused by Host shutdown.

An execution report stores the workspace, operation, request digest, workflow and plan DocumentIds, authorization identity, reserved output directory, and exit code. The physical BlobId hashes the exact canonical frame. The CanonicalDigest hashes its canonical segment. The logical DocumentId uses the existing document identity scheme with the execution-report kind and schema. The existing report RPC revision and `report` artifact still identify the exact JSON projection bytes. They are distinct from native evidence identifiers. The JSON is rendered in memory and need not exist as a terminal file.

The Host catalog exposes `native-report` as a verified raw frame. The indexed artifact store can retain both that frame and the JSON projection. Version 2 of its artifact manifest names both identities and uses a separate field for the projection digest. Version 1 retains its legacy report field unchanged. Explicit stash takes place after terminal completion and has its own retention policy. A report does not depend on the stash manifest, so the receipt may refer to the report without creating a cycle.

## Publication and retry

1. The runner finishes worker cleanup, captures final observations, and appends the terminal progress event. The event is progress evidence.
2. The Host verifies the bound source, plan and operation identifiers and validates recorded output and trace digests for files exposed from the results directory.
3. The Document Store installs the canonical frame and commits its descriptor and operation-specific immutable publication reference. Its transaction is separate from the operation ledger.
4. The operation ledger commits the terminal outcome, native report reference, public projection digest and History admission together. A visible outcome therefore has a preceding retained document.
5. An exact request replay can commit a report retained by an interrupted publisher. Under the operation lease, the Host verifies the document, authorization and exposed required artifacts and commits the same result. Request replay cannot launch work across that boundary. If publication never committed the retention reference, the operation remains governed by its earlier recovery state.

Required payload verification covers saved declarations under the results root. Large raw captures remain native files. Explicit artifact stash copies its retained payloads into the shared CAS. This ADR does not give ordinary output directories an automatic expiration policy.

## Transitional state

During execution, the epoch-6 CMake runner still uses the historical JSON checkpoint. It no longer replaces that checkpoint with a terminal report. Recovery still consumes and verifies the checkpoint. Terminal report reads and downloads use the committed native document. A mutable checkpoint and a terminal JSON projection remain different contracts. Historical report readers continue verifying exact original bytes. Profile replay and derived analysis still use their existing artifacts.
