# ADR-0008. Production workflow archive and persistence rollout

**Status.** The workflow schema, production CAS admission and explicit archive of accepted workflows are implemented. The archive can hold new canonical workflow documents and exact legacy snapshots. Execution admission, plan approvals, recovery and their writers continue to use their existing contracts. Production writer cutover remains gated.

## Dependencies and boundaries

    Contracts ← generated workflow codec ← Document Store ← Host archive service ← CLI
    Core CAS and durable files ← Document Store and artifact CLI
    SQLite catalog ← Document Store
    SQLite operation ledger ← Host admission and recovery

Capnp.Managed owns the wire operations. The official compiler and C# generator supply checked-in bindings. DocumentStore owns document identity, policy and SQLite references. Core provides the same physical CAS for document and artifact blobs in a document-aware namespace. The Host archive service reads the accepted-operation ledger in read-only mode and never admits or dispatches work. The CLI offers one explicit archive operation and uses its existing CAS collection command. The [persistence inventory](../persistence-inventory.md) identifies owners, state, identity contracts and remaining formats.

## Workflow schema registry

| Catalog codec | Wire schema | Application schema | Identity and behavior |
| --- | --- | --- | --- |
| 1 | Legacy JSON | 1 and 2 | Exact stored bytes and original LegacyDigest. Isolated legacy reader. |
| 2 | Prototype workflow from P0–P3 | 2 | Original canonical wire and existing descriptor IDs. Experimental writer and adoption tests remain under ADR-0007. |
| 3 | ProductionWorkflow Manifest, file ID 0x98b4eea7fb521be4, wire version 1 | 1 and 2 | Canonical exact wire segment with a new logical identity domain, cmwf.production-workflow. |

Production schema values cover all current planner kinds, inspect, configure, build, invoke, run, profile, profile_snapshot, format_check and ctest. Packages and benchmark execution currently compose these workflow kinds or use application commands. No extra kind is invented for them. The schema preserves node and dependency declaration order, repeated dependencies, arguments and resource order, and all known binding, filter, option, benchmark and artifact fields. Optional text distinguishes null from empty. Optional structs distinguish absence from an empty value. Timeout presence is a union, preserving the bits of an explicitly stored Float64. Maps use ordinal key order. The planner remains responsible for path authorization, host observations, graph validity and operation-specific semantics.

Each wire schema owns its file and node IDs and all assigned ordinals. Preserve existing ordinals and enum/discriminant values. Add fields at new ordinals and keep retired slots reserved. A new field needs an explicit absent/default interpretation. A new kind requires reader and planner compatibility review. Incompatible semantics use a new codec version or schema identity. Existing values retain the old reader and digest semantics. The generated source is verified with the official frontend in CI.

BlobId remains SHA-256 of the physical frame or exact JSON. CanonicalDigest remains SHA-256 of the official unframed canonical segment. DocumentId retains the cmwf-document-v1 preimage grammar from ADR-0007, with the descriptor's declared schema string, schema version and codec ID. LegacyDigest remains exact JSON SHA-256. Equivalence records preserve two separate logical IDs and a verified projection digest. They cannot authorize a plan, a dependency install, a dispatch, or a cache hit.

## Shared CAS, store admission and retention

Production archives open a **new empty CAS namespace** or a previously initialized v2 namespace. The marker is cmakeworkflow-artifact-cas-v2. The catalog is documents.sqlite with application ID 0x434d5744, user_version 2, WAL, FULL synchronization and foreign keys. The catalog and payload directories share the existing CAS lease. The P3 layout and v1 artifact namespace retain their original format. In-place v1 upgrades are deferred so an older collector can never encounter document roots in a namespace it accepts.

The new artifact CLI reads v1 and v2. A v1-only collector refuses the v2 marker before looking for candidates. The current collector holds the CAS lease while reading pinned artifacts and action roots, incorporates SQLite document references and pins, and tests shared file handles before pruning. It commits removing unrooted document descriptors before removing physical bytes. A crash between those steps leaves a reclaimable orphan. In a v2 CAS containing artifact metadata, run the combined collector through the artifact CLI. Operations with a missing or unrecognized v2 catalog fail closed before collecting. Unretained publication is eligible for collection after the writer releases its lease.

A buffered session retains a checked private snapshot and shared physical lease. A mapped session borrows from the already opened, verified handle and holds it until inspection finishes. Unix filesystems can fault when a file is truncated by a noncooperating writer with filesystem permission. The store assumes appropriate ACLs, a local filesystem and cooperating writers. Path checks reject obvious link and reparse substitutions at store access. This design does not replace the stronger workspace-anchored checks in Host publication or admission.

Files are flushed before create-only installation. The shared durable file helper synchronizes parent directories on Unix and uses write-through rename on Windows. SQLite commits after blob installation and verification. The process failure model covers interrupted stages, reclaimable orphans, a lost reply after commit and retry. Power-loss behavior depends on actual filesystem, OS and hardware guarantees and remains unverified.

## Explicit archive protocol and rollback

1. Resolve the workspace and operation through a read-only operation ledger. Require the workflow domain, bound the snapshot, and verify the original exact SHA-256.
2. Decode the legacy JSON, encode with the production schema and verify the binary representation. Compare projections after ordinal map normalization. Preserve list ordering.
3. Take the CAS lease and install both payloads create-only. Commit both catalog descriptors, the equivalence, and both archive references under workflow/workspace-id/operation-id.
4. Return the record. Retrying the same snapshot is idempotent. A reference already naming different content fails without changing the committed references. Retrying after interruption before commit can reuse the physical blobs. The source file and ledger stay intact.

    CMakeWorkflow workflow archive WORKSPACE OPERATION_ID CAS_DIRECTORY
    CMakeWorkflow cas CAS_DIRECTORY gc
    CMakeWorkflow cas CAS_DIRECTORY gc --apply

For application code, WorkflowArchive.ArchiveAcceptedAsync returns both descriptors. DocumentStore.OpenProductionStoreAsync, PublishRetainedAsync and OpenAsync provide typed publication, verified materialization and exact or projected export. Production verification reports wire, canonical and schema evidence. It leaves graph admission to the planner. JSON remains projected on demand through ExportJsonProjection. ExportStoredBytes recovers the legacy file exactly. Unknown binary fields remain in the exact wire representation.

A pre-archive copy of a quiescent legacy workspace ledger remains usable. The archive operation neither rewrites it nor creates new rows. A rollback of the archive slice uses the original workspace with the older binaries and keeps the v2 archive detached. Do not point an old collector at a v2 namespace or manually relabel it as v1. There is no tested downgrade for binary-only execution snapshots because this slice does not write them.

## Gates for the next slice

The archive verifies persistence and equivalence without changing execution authority. A production writer cutover still requires an atomic admission/reference protocol, downgrade fencing for the operation ledger, verification of old and new Host and SDK recovery with real interrupted operations, and Windows and deployment-filesystem checks. Additional immutable document families need individual approval, identity, compatibility and retention reviews. The existing transactional journals and mutable indexes retain their owners. No legacy data or obsolete production writer has been removed in this slice.

## Native execution follow-on

[ADR-0009](0009-binary-workflow-execution.md) defines the explicitly initialized native workflow execution workspace. This archive contract remains in force for historical legacy operations.
