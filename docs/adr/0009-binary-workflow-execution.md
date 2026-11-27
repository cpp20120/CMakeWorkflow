# ADR-0009. Native workflow admission, execution and recovery

**Status.** Opt-in native execution uses the production CMake planner, workflow runner, operation ledger and recovery coordinator. Existing JSON writers and approval policies continue to apply to legacy workflows.

## Formats and version fencing

| Boundary | Representation |
| --- | --- |
| Public transport | JSON-RPC protocol 1. Native execution uses `execution/start` with domain `cmake-capnp-v1`. The SDK negotiates `execution/cmake-capnp-v1`, `workflow/prepareBinaryManifest` and, for a native Git experiment, `experiment/binary-v1`. |
| Submitted workflow | Bounded, unpacked canonical production workflow frame. The exact frame SHA-256 is also the workflow report provenance hash. |
| Immutable execution documents | Shared artifact CAS marker `cmakeworkflow-artifact-cas-v2`, application ID `0x434d5744`, catalog epoch 3 for fresh execution stores. Archives and historical binary execution stores continue to use epoch 2. |
| Operation ledger | Application ID `0x434d5742`. Fresh ledgers use epoch 3 and store a retained plan reference. Existing epoch-2 binaries use the original inline binding. |
| Managed tooling | Native environment locks, provisioning plans, dependency plans and receipts have separate Cap’n Proto schemas and identities. Host-wide locks and receipts use the existing managed tooling store. |
| Controller | Experiment plan schema 3 carries its binary workflow frame and the exact approved source and execution metadata in the existing controller JSON envelope. External Git, ZIP, CMake and vcpkg formats stay compatible. |

`workflow init-binary WORKSPACE` claims `.cmakeworkflow/operations.sqlite` as a directory. The ledger remains at `execution-v2.sqlite` within that directory for compatibility with the existing path convention. SQLite application ID and schema epoch identify its contents. P4.A binaries attempt to open the directory as a database file. P4.B binaries refuse a new ledger epoch before writing. A P4.B collector refuses catalog epoch 3 before pruning. Current collectors understand both admitted epochs under the same CAS namespace lease.

A legacy operations file and earlier binary ledgers are preserved in place. To use source-bound native execution and native experiments, initialize a fresh workspace. Restore stopped native workspaces as a unit with the execution-document CAS, run evidence, source store and required managed installations. Exported workflow JSON carries workflow data only. Approval and recovery authority stay in the original ledger.

## Publication, dispatch and recovery

1. Generic upload stages a content-addressed `.capnp` file in the caller’s authorized manifest base. The SDK first probes by exact SHA-256. The Host repeats bounded SHA-256, canonical wire and schema checks for both probing and upload. The Git experiment path retains the existing resumable source ZIP upload and stages a typed workflow beside verified Lore materialization.
2. Native admission checks request identity before reading the stage on a retry. Source-bound admission verifies the declared Lore identity, graph and managed source layout before committing. The same transaction stores operation identity, selection, history policy, source identity and the admitted workflow descriptor. The legacy workflow BLOB is empty.
3. The binary workflow descriptor is rooted before the operation transaction. Execution opens the descriptor from CAS and gives the typed object to the shared planner. For source-bound execution, the planner uses a verified, operation-specific Lore checkout and a new build tree. Recovery verifies every materialized source file and its executable bit.
4. A fresh executor publishes the resolved plan binding with a retention reference before the authorization transaction. The ledger commits its DocumentId, approval identity and policy with the one-way dispatch transition. The binding includes the admitted workflow identities, request and operation, selected graph, paths, parallelism, source identity, and resolved environment, dependency and cache identities. The workflow document supplies declarations such as arguments, constraints and outputs. Ambient secrets and live process state remain in memory or existing lifecycle evidence.
5. Resume verifies the retained plan and compares a fresh resolution against the exact authorized bytes before claiming its one-shot attempt. The runner checkpoint gate binds the last written checkpoint to the same authorization. On an uncertain launch, the operator identifies the single retryable running node, supplies the bound checkpoint digest and confirms quiescence. The coordinator uses the established reconciliation, resume admission and execution transitions.

An interrupted CAS publication may retain an unused reference. Workflow and plan references survive terminal state. Scope general expiration and dependency disposal with deployment and GC work. The fault tests cover process interruption. Power-loss behavior requires separate infrastructure verification.

## Identity and authority

| Identity | Binding and scope |
| --- | --- |
| Workflow BlobId | Exact frame. Used in admission integrity and reports. |
| CanonicalDigest and DocumentId | Official canonical segment hash and logical identity over kind, schema, codec, policy epoch and content digest. These establish content identity. |
| Request digest | A length-delimited preimage binds admitted frame, manifest base, output directory, jobs, selection and history policy. Source-bound requests have a distinct domain that adds the verified source identity. |
| Ledger epoch-2 dispatch authorization | Original `cmwf-cmake-dispatch-binding-v1` domain and exact frame. |
| Ledger epoch-3 dispatch authorization | `cmwf-cmake-dispatch-binding-v2` domain and exact retained plan frame. SQLite policy 2 also binds the plan DocumentId and operation. |
| Environment lock | `cmwf-managed-toolchain-environment-v1` and exact frame. Verification resolves the supported catalog and compares the complete canonical frame, including compiler/linker distribution, sysroot, ABI, generator and policy. |
| Tooling plan | `cmwf-tooling-provisioning-plan-v1` and exact frame. Setup recomputes the plan, compares the reviewed bytes and digest, then honors existing execution consent and post-install checks. |
| Dependency plan | `cmwf-vcpkg-dependency-plan-v1` and exact frame. Binds vcpkg executable and checkout, manifest/configuration hashes, registry policy, environment lock, commit, source snapshot and triplets. |
| Provisioning receipt | `cmwf-vcpkg-provisioning-receipt-v1` and exact frame. Binds the approved dependency plan, installed tree, chainload script and captured download tree where applicable. The first receipt is pinned outside the candidate installation and survives an interrupted retry. |
| Experiment approval | Controller schema 3 uses `cmwf-experiment-approval-v1` and the exact approved controller envelope, including commit, source archive, native workflow, dependency identity and Host binding. Dependency approval is a separate explicit step. |

All listed identity domains append LF before the hashed payload. Existing legacy exact-byte digests and policy meaning remain intact. Neither archive equivalence nor integrity grants execution authority. Linux tool resolution enforces the current managed toolchain and dependency installation policy. The Action Cache retains its own identity policy for the effective invocation, inputs, tree generation and toolchain. [Persistence inventory](../persistence-inventory.md)

## Scope and verification

See the [P4.C and P4.D verification report](../p4-binary-execution-verification.md) for reproduction commands, test evidence and outstanding scope. P4.E covers report, History, journal and cache metadata formats. P4.F covers deployment migration, Windows verification and mixed-version operations on supported filesystems. P4.G covers changes to the default writer and legacy retirement. P5 covers performance analysis and optimization under matched workloads.
