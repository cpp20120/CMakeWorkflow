# ADR-0007. Experimental immutable workflow document store

**Status.** Implemented as a local experiment. Production adoption is deferred to P4.

## Architecture and production audit

The store reuses Core’s ArtifactStoreFiles and DurableByteWriter for validated digest paths, an interprocess lease, create-only installation and duplicate verification. It uses a dedicated payload CAS. SQLite holds mutable metadata. The only production-project change grants internal access to the CAS primitives. Capnp.Managed and its compiler have no storage dependency. The workflow adapter owns schema bindings and DTO projection. The store owns codec dispatch, identity, validation policy, publication and retention. The fixed registry and production source-generated JSON context work with NativeAOT.

| Production component | Decision |
| --- | --- |
| Artifact CAS, action entries, operation pins, lease and GC | Reuse lease and file primitives. Action entries and protected operation roots are foreign to the dedicated document namespace. The collector rejects their presence. |
| ManifestPublication, WorkflowManifestBytes and WorkflowDefinitionJson | Host publication and admission use exact JSON SHA-256. Recipe output includes a final LF. Reuse the DTO context and preserve the original input on import. |
| Scripting snapshots, dependency approvals and ManagedEnvironmentLocks | Their provenance and approvals carry their own identities. Each needs a separate migration contract. |
| HistoryStore, PipelineLedger, host admission and recovery | Their mutable transactions and journals retain their existing owners. The document catalog is independent and uses the existing SQLite package pattern. |

## Identity and schema

All four digest types use lowercase 64-character hex for serialization. Constructors normalize case and enforce the digest grammar. Equality is over the digest within its declared type.

| Type | SHA-256 input |
| --- | --- |
| BlobId | Exact stored bytes, including any frame header or JSON whitespace |
| CanonicalDigest | Official canonical unframed Cap’n Proto segment, preserving the P0–P2 contract |
| LegacyDigest | Exact original JSON bytes, preserving the production identity |
| DocumentId | UTF-8 fields joined by LF, with no final LF |

The logical preimage fields are **cmwf-document-v1**, **workflow-manifest**, **cmwf.workflow**, decimal schema version, decimal codec ID, decimal codec version, and canonical digest for binary or blob digest for JSON. The descriptor version and both codec versions are 1. JSON is codec 1 and supports schema versions 1 and 2 under its exact-byte policy. Binary is codec 2 and supports schema 2. DTO encoding sorts map keys ordinally and dependency sets by declaration order. Existing canonical binary payloads are imported as given, including their known and unknown fields. The payload is an official single-segment unpacked frame. The descriptor also carries the stored length. Timestamps and provenance do not enter deterministic identity. A binary import with additional wire fields can have the same projected DTO and a different canonical identity. Migration equivalence links separate legacy and binary logical IDs with SHA-256 of the normalized projection.

## Publication and recovery

The root contains documents.sqlite and payload/. Catalog application ID is 0x434d5744 and schema version is 1. Unknown catalog versions fail explicitly. Strict tables hold descriptors, named retention roots and equivalence records. Foreign keys restrict deletion of retained documents and cascade removal of their equivalence edges. The catalog uses WAL and synchronous=FULL on a local filesystem. Operations take the global CAS lease before opening the catalog or starting a transaction.

1. Encode or copy bounded input and validate it. Compute the exact and, where applicable, canonical digests.
2. Take the CAS lease. Stage and flush the payload. Install it create-only and verify the installed bytes. For a duplicate, verify the existing blob.
3. Insert or compare the descriptor inside a SQLite transaction. Retained publication sets its reference in the same transaction. Rehearsal inserts both documents and the equivalence in one transaction.
4. Commit SQLite. This is the point of visibility for a new document and reference. Retrying after a lost response verifies the blob and compares the descriptor before returning it.

The shared publication primitive syncs parent directories on Unix and uses Windows write-through rename. Together with the file flush and SQLite settings, these are the durability operations performed. They do not establish power-loss durability for all filesystems or controllers. A process stopped before commit can leave a stage or an orphan. A process stopped after commit leaves a visible document. Collection prunes stale stages and orphan blobs under the same CAS lease. It commits catalog pruning before removing physical files, so failure between those steps leaves a reclaimable orphan. An externally damaged or missing committed blob fails verification on open.

The tests inject exceptions at nine publication checkpoints, during reference deletion and between catalog pruning and file removal. Separate tests kill publisher subprocesses during staging, after physical publication and after commit. They exercise retry, reopen, and collection racing with a publisher at the pre-commit checkpoint. These tests establish behavior under the documented process-failure model.

## Ownership, retention and verification

A session resolves its descriptor and takes a shared read handle under the global lease, then releases that lease before reading and verifying. The buffered owner keeps a private copy and the shared handle. The mapped owner maps the opened handle. Synchronous callbacks receive scoped borrowed readers and return independently owned results. Disposal prevents new borrows immediately and releases the shared handle once all active callbacks finish. Multiple readers and mappings may coexist. Each open session consumes a file descriptor or handle.

Named references and pins retain content. An equivalence entry has no retention role. The collector takes the global lease, checks roots and attempts an exclusive open before pruning. A shared session prevents that exclusive open across cooperating processes. Logical deletion of an unrooted row can coexist with a live reader. Destructive collection waits for the last reader. Plain publication produces an unrooted document. Use PublishRetainedAsync to commit visibility and retention together.

On Unix the store creates its root with owner-only permissions. Existing directories and Windows depend on installation permissions and ACLs. Path checks reject symlinks and reparse points, and mapping uses the opened file handle. The model assumes cooperating processes and a local filesystem implementing .NET file-sharing locks. A writer with sufficient filesystem permissions can still alter or truncate the mapped file. Unix truncation can cause an OS fault independently of managed bounds checks. External files receive bounded buffered access.

| Policy | Evidence |
| --- | --- |
| Initial binary publication or import | Official canonical form, exact digest, checked typed projection and offline graph checks |
| JSON import | Exact legacy digest and recognized source-generated DTO with known, unique JSON properties |
| Full open (default) | Exact length and SHA-256. Binary adds canonical digest, re-canonicalization, wire structure, typed projection and graph checks. JSON adds DTO and version checks. |
| Stored-bytes open | Exact length and SHA-256. A mapping must also parse framing. Typed access still checks field bounds and formats. |
| External rehearsal | Bounded buffered input and exact legacy SHA-256, plus comparison of normalized projections before equivalence commit |

Verification is bound to the owner, the exact bytes and the fixed schema and resource limits. The store uses a 16 MiB limit for a binary segment, 16,384 for text bytes and list counts, and depth 32. Reused binary inspection parses the fixed frame header and accesses checked readers without full revalidation. Full binary materialization reuses completed wire evidence while performing typed projection. Independent opens re-verify. Offline graph validation includes IDs, dependencies, contexts, run sources, artifact references, positive finite timeouts and cycle detection. The production planner owns host-specific and complete domain admission.

## Workflow compatibility and API

| Field or operation | Binary schema v2 |
| --- | --- |
| SchemaVersion, Contexts, Nodes | Schema 2 only. Preserves node declaration order. |
| Context | BuildDirectory, Configuration, ConfigurePreset, SourceDirectory, Mode, Generator, ProvenancePolicy and CompilerCacheProfile. Rejects Toolchain and BuildVariant. |
| Nodes | Id, Kind, Context, DependsOn, Arguments, WorkingDirectory, Environment, Resources, Exclusive, TimeoutSeconds, Inputs, Outputs and ActionCache. Constraints supports CpuSet. Rejects Profiler, Metrics, BenchmarkTrial, SnapshotExecutable, SnapshotSha256, HarnessDirectory and nonempty Options. |
| Artifact inputs and outputs | Input Path, Sha256, From, Artifact and Snapshot. Output Name, Path, Root and Fresh. |
| configure, build, run, ctest | Supported with operation-specific Target, From, TestRegex and LabelRegex. |
| inspect, invoke, profile, profile_snapshot, format_check | Rejected for binary conversion. |
| Schema 1 and 2 legacy JSON | Exact archival and DTO parsing for recognized fields. Conversion requires the binary subset. |
| Unknown binary fields | Preserved by exact import and export. Generic wire inspection exposes them. |

    DocumentStore store = await DocumentStore.OpenStoreAsync(experimentalRoot, ct);
    DocumentDescriptor descriptor = await store.PublishRetainedAsync(workflow, "trial/head", ct);
    using DocumentSession session = await store.OpenAsync(descriptor.Id, DocumentAccess.Mapped, ct: ct);
    int count = session.InspectWorkflow(static view => view.NodeCount);
    WorkflowDocument editableCopy = session.Materialize();
    byte[] projection = session.ExportJsonProjection();
    byte[] exactPayload = session.ExportStoredBytes();
    VerificationReport evidence = session.Verification;
    MigrationResult equivalent = await store.RehearseMigrationAsync(jsonPath, expectedLegacyDigest, ct);

Rehearsal uses the external input read-only and compares normalized values before recording equivalence. FindEquivalenceAsync exposes the recorded comparison. ExportJsonProjection serializes the materialized DTO on demand. Binary DTO projection exposes known schema values, and archived JSON projection exposes the parsed DTO. InspectWire and InspectStoredBytes supply scoped access to wire fields and the exact representation. ExportStoredBytes returns an owned copy of every byte, including the original whitespace in archived JSON and unknown fields in a stored binary frame.

## Platform and P4

Linux x64 NativeAOT was built and executed locally with warnings treated as errors. It uses a bundled native SQLite provider and system libraries. Experimental CI covers Linux and Windows x64 store tests and NativeAOT and runs the official C++ oracle on Linux. Windows outcomes remain to be verified in CI. See the [P3 performance report](../capnp-p3-performance.md) for measurements and commands.

P4 should start by inventorying real manifests and rehearsing conversion read-only. Complete schema and planner coverage for the required production operations. Define a versioned producer and consumer rollout, preserve exact legacy identity in existing approval contracts, and keep both references auditable. Verify Windows recovery and physical leases, validate durability and retention on deployment filesystems, and rehearse rollback and mixed-version recovery. Production JSON remains authoritative until those checks pass.

The production workflow archive and separate v2 CAS namespace are described in [ADR-0008](0008-production-persistence-migration.md). This ADR continues to specify the prototype codec and catalog.
