# P4.C/D residual work, persistence and deployment verification

The task started at `6a29258021af0e203c4a828dc8683213a1850e75`. Its `git archive` has SHA-256 `210b550032ec3d30b96bbea1a189982afe35b697fabbd0ecad6f1b9dd6797d70`. A Linux x64 NativeAOT baseline was built from its extracted source. The branch advanced to `0771550` during the work. The original user files `graphify-out/`, `poc/` and `usings.txt` were present when work began. The event ledger, SSH coverage and current verification documents are uncommitted working-tree changes. The source baseline, relevant executable builds and verification logs are preserved in the ignored `out/p4-ef-verification.zNU9Md/` directory, with a SHA-256 manifest for archived source and runtime files.

## Delivery status

| Slice | Status | Verified or outstanding scope |
| --- | --- | --- |
| P4.C.R | Partial | Declared Lore inputs, mapped results, cache policy and guarded recovery are implemented. See the source path policy in ADR-0011. The local source fixture proves input provenance, output catalog verification, traversal and missing input refusal, overlap refusal and recovery. The SSH fixture stages a binary source bundle, builds and tests with Ninja, kills the Host and retrieves verified outputs after restart. Source, dependency and path variants need a larger matched cache matrix. |
| P4.D.R | Partial | Fresh managed installation, CPack version, binary environment lock, vcpkg receipt, configure, build and test verified. Actual termination and approved retry verified at tool publication, lock publication and receipt publication. Download, extraction, package restoration, post-install verification and the two sides of the receipt pin require independent interruption coverage. SSH testing exercised several approval negatives, but the expanded matrix remains open. |
| P4.E.0 | Complete and verified | The inventory covers private and public persistence families, current authority and migration prerequisites. |
| P4.E.1 | Partial | A canonical immutable artifact manifest records verified stashed operation evidence. Its pin is SQLite. Native execution, benchmark and profiling reports plus terminal report publication via the Document Store remain open. |
| P4.E.2 | Partial | Existing History indexing and retry were verified during regression and the remote experiment. Native report evidence, History versioning and native import remain open. |
| P4.E.3 | Partial | Epoch 5 adds typed append-only workflow events and artifact descriptors under the operation ledger. Checkpoints, process lifecycle journals and reconciliation files retain their existing authority. The actual Host termination and stopped-store restoration preserved the committed event cursor and appended the resume events in order. Full recovery migration and crash coverage remain open. |
| P4.E.4 | Partial | Opt-in SQLite Action Cache uses existing physical CAS and identity policies. Current CLI verified legacy-compatible keys, actual older entries, missing and corrupt data, GC and eviction. Bulk transfer into an indexed namespace and crash/concurrency coverage across all publication boundaries remain open. |
| P4.E.5 | Partial | Controller and pipeline ownership audited. The existing binary Git experiment still has a JSON controller envelope and JSON projections. Native controller document, pipeline evidence migration and dedicated crash coverage remain open. |
| P4.E.6 | Partial | The new event stream uses the existing RPC projection. Artifact JSON exposes the retained manifest BlobId. Full report, History and controller projection changes depend on their migrations. |
| P4.F.1 | Partial | Artifact, document, action and pinned payload roots share one namespace lease. GC protects active plan and workflow documents. Independent Lore, tooling, History, pipeline and backup retention remain open. |
| P4.F.2 | Partial | Read-only layout inspection and mixed-epoch reads work. There is no production upgrade planner or durable apply/reconcile workflow. |
| P4.F.3 | Partial | Actual old binaries and collectors exercised. The stopped local fixture and the SSH source-bound fixture were restored to their original canonical paths and resumed. The SSH copy included Lore, execution CAS and the indexed Action Cache. A managed source-bound restore with native checkpoint, report and History evidence remains open. |
| P4.F.4 | Partial | Linux process kill, active GC, resume, guarded publication, corruption refusal and stopped-store copy tested. Power loss, filesystem fault injection and full failure matrix remain open. |
| P4.F.5 | Blocked | Actual Linux x64 NativeAOT and a Linux x64 SSH Host were available. Windows, macOS and other architectures had no execution runners for this work. |
| P4.F.6 | Partial | The approved managed Git experiment passed on real SSH with recovery, existing History indexing and artifact download. The requested combined upgrade, full native evidence, retention and restore scenario needs the remaining persistence work. |

## Storage and identities

- Scope of the new schema and immutable artifact receipt is in [ADR-0010](adr/0010-native-artifact-evidence-and-action-index.md). The artifact evidence is a canonical frame with an exact physical BlobId. It records the producing legacy report SHA-256 and ordered paths, roles, lengths and payload hashes. Execution report DocumentIds remain pending.
- Native event rows and their ordered artifacts commit together in the operation ledger. The checked path, write order and restart semantics are in [ADR-0012](adr/0012-native-workflow-event-journal.md). Checkpoint and terminal report remain `run.json` and keep exact-byte identities. Process lifecycle audit retains JSONL. Existing Host and History SQLite ledgers keep their separate transaction boundaries.
- Old action key preimages remain valid. Source-mapped actions also bind the Lore snapshot ID and validated effective paths. See [ADR-0011](adr/0011-source-artifact-paths-and-deployment-fencing.md). The indexed store verifies and publishes the payload before committing its action row. Equivalent rows are idempotent. Conflicting outputs are refused.
- Shared-CAS collection stops if the indexed reference metadata or a retained artifact manifest is unreadable. Unpin retains the immutable receipt and allows unreferenced payloads to be collected. No new cross-store collector or retention release policy was enabled.
- The [persistence inventory](persistence-inventory.md) contains the identity dependency map and remaining JSON usage. Remaining authority includes execution reports and checkpoints, legacy/process journals, History evidence, controller state and pipeline projections. Public JSON-RPC, CLI projections and external tool formats are intentional.

## Inspection, backup and version compatibility

Run `CMakeWorkflow workflow storage-inspect /absolute/workspace` to inspect the workspace operation ledger and document catalog. The command identifies the store paths needed by the operation. Record separate configured History and artifact CAS paths when used.

For the tested stopped-store copy, the Host was terminated at a known pre-external-launch boundary, the remote channels and Local RPC clients were closed, and workspace files were copied together. Restore to the same canonical path before starting the matching Host. The recovery procedure verifies the retained plan and checkpoint and requires explicit reconciliation and resume approval. The local fixture excludes external stores. The SSH source-bound fixture also copies and verifies the operation’s workspace-scoped Lore store, and restores the indexed Action Cache with the workspace before collection and resume. A deployment backup must stop all relevant Hosts, workers, provisioning writers, collectors and History indexers, then preserve operation data and results together with exact source, tooling, artifact and History roots. Keep SQLite sidecars with the stopped-store snapshot. The rehearsal does not establish the consistency of a live copy or a copy made while any participant can write.

| Executable or layout | Result |
| --- | --- |
| Pre-P4 `b56f060` | Rebuilt from revision source using Linux x64 NativeAOT. The actual Host rejected the current fenced layout. A separate P4.A executable was unavailable for this run. |
| Archived P4.B inline-binding build | Rejected the fresh ledger and newer document catalog collection. Current CLI opened a fixture created using this older binary. |
| Rebuilt P4.C/D `6a29258`, ledger epoch 3 | Current CLI read historical data and reused an actual cache entry created by this executable. Its Host rejected the newer ledger. |
| Epoch 4 and 5 | The preserved epoch-4 Host rejected epoch 5. The current Host read epoch-4 operation state and replayed its original JSONL feed, as with epochs 2 and 3. |
| Existing legacy data | Current binary retained the legacy writer and ran the workflow/report/history and Action Cache regression path. An executable rollback requires a compatible original store or restored copy. Schema rollback and reuse of native authorization in legacy stores remain unsupported. |

## Reproduction and test evidence

Use two separate build directories so the production executable contains no test fault hooks.

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror --artifacts-path /tmp/cmwf-native-build -o /tmp/cmwf-native
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -p:PublishAot=false -p:RecoveryFaultInjection=true --artifacts-path /tmp/cmwf-fault-build -o /tmp/cmwf-fault
dotnet build CMakeWorkflow.Host.Tests -c Release -r linux-x64 -warnaserror
suite=CMakeWorkflow.Host.Tests/bin/Release/net10.0/linux-x64/CMakeWorkflow.Host.Tests.dll
dotnet "$suite" --source-binding-tests
dotnet "$suite" --binary-operation-kinds-tests /tmp/cmwf-native/CMakeWorkflow
dotnet "$suite" --binary-workflow-wire-tests /tmp/cmwf-native/CMakeWorkflow /tmp/cmwf-fault/CMakeWorkflow
dotnet "$suite" --indexed-action-cache-tests /tmp/cmwf-native/CMakeWorkflow /absolute/older/CMakeWorkflow
dotnet "$suite" --historical-action-cache-tests /tmp/cmwf-native/CMakeWorkflow /absolute/older/CMakeWorkflow
dotnet "$suite" --managed-provisioning-fault-tests /tmp/cmwf-native/CMakeWorkflow /tmp/cmwf-fault/CMakeWorkflow /absolute/disposable-managed-store
CMWF_SSHNET_TEST_BINARY=1 bash tests/infrastructure/sshnet/run-tests.sh
```

The native experiment and source artifact SSH cases are also individually selectable through the suite argument `--sshnet-live-lifecycle-tests` with `binary-experiment` or `binary-source-artifacts`. The managed provisioning case expects a disposable store with Linux Clang and vcpkg already installed and no published Ninja Multi-Config lock. It creates a separate empty store for the tool-install interrupt. The [prior binary execution report](p4-binary-execution-verification.md) documents the manual Git experiment plan, dependency approval, launch, recovery, result and download commands. The SSH.NET harness uses a loopback Docker sshd with a per-run trusted host key. It runs the NativeAOT controller and Host and a framework-dependent Host with test-only interruption hooks.

Production workflow and Document Store suites passed with official Cap’n Proto 1.2 CLI and C++ oracle. An actual stashed artifact manifest was decoded with `capnp convert binary:text ... ArtifactManifest`, then its canonical segment was verified byte-for-byte with `capnp convert text:canonical`. The fixture's manifest BlobId was `a9386458a37524b48b3357eda41950da6a4b5edb11f05fddd64f6c55b85494c1`. All nine operation kinds, Host wire, History index and legacy/native cache regression suites ran on Linux. The epoch-5 Host and controller passed the managed Git benchmark with explicit environment and dependency approval, concurrent dependency application, artifact download and post-crash History indexing.

## Filesystems and platforms

| Platform | Actual runtime coverage |
| --- | --- |
| Linux x64 | NativeAOT CLI and Host, managed .NET fault build, local CMake and SSH.NET. The binary recovery, active GC, stopped copy and mixed-version wire suite ran on both local tmpfs and ext4. The SSH process fixtures used the local Docker filesystem. |
| Windows x64 | No runtime or NativeAOT execution. Job Object behavior and reparse-point handling still require real execution. |
| macOS | No runtime, Lore, recovery or NativeAOT execution. Profiler capabilities remain platform-specific. |
| Other architectures, network volumes and managed host filesystem variants | No runtime or durability verification. |

The tested contract covers process termination and restart with the operating system still running. Full synchronous SQLite commits, append-only SQLite transitions and same-directory create-only publication preserve the documented ordering. Separate database commits and file publication remain distinct. Power loss, parent-directory persistence on storage failure, device write caches, shared-filesystem locking and unplugged or full volumes require dedicated tests on supported deployment storage.

## Measurement and footprint

The Linux x64 NativeAOT executable was 39,909,192 bytes, compared with 39,771,848 bytes for the archived baseline. The publish tree including symbols and companion libraries was 204,621,291 bytes, compared with 204,170,890 bytes for the baseline. Managed tool archives and workspace payloads were outside both trees.

The startup probe `dotnet scripts/measure-persistence-startup.cs CURRENT` observed 25.226 ms for CLI catalog, 22.421 ms for Host readiness and 29,470,720 idle RSS bytes. The seven-sample `--measure-binary-workflow CURRENT PREVIOUS` run under test load observed 842 bytes and 2.697/68.655 ms median admission/terminal time for baseline JSON, 845 bytes and 2.069/68.217 ms for current JSON, and 408 bytes and 5.816/77.582 ms for binary. These are local probe results under the recorded workload.

## Modified projects and interfaces

The changes from the starting revision cover workflow wire schemas and generated bindings, CMake planning and execution, Host admission, recovery and report-backed artifact access, Core CAS metadata, Document Store initialization, Tooling provisioning fixtures, and the CLI. The latest changes extend the Host epoch schema, the runner event sink, the Host's event replay and the SSH harness. Host, Document Store and codec suites cover the affected boundaries. The schema regeneration check includes the new artifact manifest in the existing Cap’n Proto workflow. The remaining platform and deployment runs are separate from that configured check.

## Work remaining

Finish full native terminal and benchmark/profiler evidence publication, History versioning and retries, checkpoint and process recovery migrations, controller evidence, coordinated retention and backup, and an enforceably quiescent workspace upgrade with actual old executable rehearsals before approving any default cutover. Complete the unavailable platform and filesystem fault testing.

P4.G covers production default writer changes and retirement of supported legacy writers after the release gates. P5 tracks matched cold and hot end-to-end workloads, journal transaction cost and contention, report decoding and materialization, steady-state Host memory and mapping lifetimes, action lookup and GC cost, mixed-store deployment footprint, and remote admission and recovery timing. Keep reproducible baseline workloads and symbols for diagnosis.
