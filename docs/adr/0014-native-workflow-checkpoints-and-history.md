# ADR-0014 — Native CMake checkpoints and History evidence

## Workflow checkpoint authority

Fresh binary CMake workflow ledgers use epoch 7. The ledger stores the latest generated Cap’n Proto `ExecutionCheckpoint` frame and its exact SHA-256 in `workflow_native_checkpoints`. The row binds the workspace, operation, request, workflow DocumentId, plan DocumentId, dispatch identity, results reservation, runner state, generation and the event cursor observed at the checkpoint transaction. The file system still owns result files and process effects.

The runner writes the ledger checkpoint before emitting the corresponding events. The dispatch gate then verifies the checkpoint and records its digest in the existing write-ahead binding. The worker launches only after the gate commits. Related but distinct commits allow the journal cursor to advance beyond the cursor recorded in a checkpoint. Recovery checks that the stored cursor is at most the committed event tail and continues append-only replay from that tail. An event and a launch-intent checkpoint do not prove that the operating system started a process.

The operator digest for epoch 7 is SHA-256 of the exact stored checkpoint frame. Reconciliation verifies this digest against both the write-ahead binding and current checkpoint, requires quiescence and preserves the previous node evidence. The revised checkpoint and immutable reconciliation decision commit in one transaction. Resume admission verifies the plan and the reconciled checkpoint. Resume execution claims its one-shot attempt and checks the reconciled checkpoint against the original dispatch digest. The runner verifies admitted graph, source and toolchain state before any retry. Earlier binary epochs and legacy workflows keep their JSON checkpoint and digest semantics.

The separate process domain still uses its existing process checkpoint, launch evidence and append-only JSONL. These contracts need a separate migration before P4.E.3 can close. External process creation remains outside the SQLite transaction.

## History evidence

The existing History database uses schema 8 and adds `workflow_native_sources`. It stores the producing operation, report DocumentId, BlobId and schema version with the derived run rows. Schema 7 remains readable. An indexed native report identity cannot be replaced by a legacy import of the same directory.

History verifies the document and terminal binding through the Host. It reads the typed workflow report directly, extracts benchmark, resource and environment observations, and uses the existing History writer and query tables. Observed output files and external harness files remain separate from the terminal source identity. Profile analysis can still consume its existing supplemental analysis artifacts. Legacy indexing continues to read and verify original report bytes. Import commit and Host acknowledgement are separate. After a crash between them, an eligible retry verifies the same native document and uses the existing derived row when it matches.
