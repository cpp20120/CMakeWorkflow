# P4.E native persistence verification

Work started at `07715508124cc829a38147016ea16bd98d3a5214` with the epoch-5 event implementation, tests and documentation already present in the working tree. The prior executable archive remains in `out/p4-ef-verification.zNU9Md`. Epochs 6 and 7, their runtime libraries, test logs and checksums are preserved in `out/p4-e-native-verification.xTFAkd`. This work changes fresh opt-in binary CMake workspaces. The legacy writer remains the default.

| Slice | Status | Scope |
| --- | --- | --- |
| P4.E.0 | Complete and verified | The existing inventory identifies the changed terminal report, History and CMake checkpoint authority. |
| P4.E.1 | Implemented with outstanding verification | Generated terminal reports, typed benchmark and capture data, distinct physical and logical identities, transactional terminal references and versioned artifact manifests. Tested success, failure and cancellation, interruption before report publication and before terminal commit, guarded replay, corrupt or missing documents, JSON projection and official canonicalization. Derived profile analysis and the full publication fault matrix remain open. |
| P4.E.2 | Implemented with outstanding verification | The existing History writer indexes typed reports and retains explicit native provenance alongside legacy runs. Tested missing evidence, retry and termination between index commit and acknowledgement. Wider comparative-query, profile-analysis and indexing-boundary coverage remain open. |
| P4.E.3 | Partial | Epoch 7 persists generated CMake checkpoint frames and their digests in SQLite and commits reconciliation with its new checkpoint generation. The process domain keeps its JSONL launch/exit evidence and existing checkpoint and termination protocols. |
| P4.E.4 | Partial | The earlier indexed Action Cache remains available. Explicit import of historical references and additional crash and concurrency coverage remain open. |
| P4.E.5 | Partial | The schema-3 experiment controller envelope and its approval semantics remain in use. Native controller documents, mutable controller lifecycle, applicable private pipeline evidence and scripting persistence remain open. |
| P4.E.6 | Partial | Existing report inspection and the artifact API serve typed report evidence and verified JSON projections. The process, controller and historical JSON contracts remain classified in the inventory. |
| P4.E.INTEGRATION | Partial | Linux NativeAOT, the approved managed SSH experiment, Host termination, typed History and official Cap’n Proto were exercised within the implemented scope. Process and controller persistence still prevent the complete native-only scenario. |

The [terminal report ADR](adr/0013-native-terminal-workflow-evidence.md) defines BlobId, CanonicalDigest, DocumentId, both artifact manifest versions and publication ordering. The [checkpoint and History ADR](adr/0014-native-workflow-checkpoints-and-history.md) defines write-ahead ordering, event cursors, reconciliation, and the separate History commit and acknowledgement. The [persistence inventory](persistence-inventory.md) tracks the remaining JSON writers.

The terminal report and checkpoint have different storage authorities. The report is an immutable CAS document retained before terminal commit. The checkpoint frame and its monotonic generation are in the SQLite operation ledger. Fresh native CMake workspaces do not need a physical `run.json` for terminal reads, History indexing or CMake resume. Earlier workspaces retain their original JSON reader and identity policies. A virtual `run.json` artifact serves the existing download and controller presentation clients. Profile replay and analysis still consume existing capture sidecars, alongside typed capture attributes stored in the report.

## Format matrix and checks

| Guard | Fresh CMake execution | Historical compatibility |
| --- | --- | --- |
| Operation ledger | Epoch 7. Checkpoint frame, exact SHA-256, generation and observed journal cursor live in a SQLite row. | Epochs 2–6 retain their original authority. Epochs 5–6 retain SQLite event replay. |
| Execution CAS | Catalog epoch 4, native report codec 5. Artifact CAS stores the canonical version-2 stash manifest with independent native and projection identities. | Catalog epochs 2–3 and version-1 artifact manifests remain supported. |
| History | Schema 8, with terminal evidence references tied to derived run rows. | Historical JSON path and exact SHA-256 remain artifact evidence. Schema 7 can still be queried. |

The epoch-7 wire fixture verified that retained workflow and plan bytes survive collection, that an earlier Host cannot write the fresh ledger, and that a restarted Host resumes an explicitly reconciled operation after actual process termination without replaying a completed node. Earlier executables exercised the old workspace paths and verified fence errors. The official compiler decoded and canonicalized production checkpoint, terminal report and artifact frames. The production workflow and Document Store also passed their C++ oracle suites. Host, legacy History, process recovery, contracts, pipeline coordinator, Tooling, managed Cap’n Proto and code generation regression suites passed. Indexed cache and actual historical cache reuse passed on the native execution path.

The terminal and History fault fixtures distinguish operation completion from History success. A retained native report can complete the original terminal transaction on exact replay. A missing report prevents terminal evidence verification. A crash after the History commit leaves an indexing claim that can be retried. Refusing a missing History source leaves the completed operation intact.

## Reproduction

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror --artifacts-path /tmp/cmwf-e-native-build -o /tmp/cmwf-e-native
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -p:PublishAot=false -p:RecoveryFaultInjection=true --artifacts-path /tmp/cmwf-e-fault-build -o /tmp/cmwf-e-fault
dotnet build CMakeWorkflow.Host.Tests -c Release -r linux-x64 -warnaserror
suite=CMakeWorkflow.Host.Tests/bin/Release/net10.0/linux-x64/CMakeWorkflow.Host.Tests.dll
CMWF_TEST_NATIVE_HISTORY=1 dotnet "$suite" --native-report-tests /tmp/cmwf-e-native/CMakeWorkflow /tmp/cmwf-e-fault/CMakeWorkflow /absolute/capnp CMakeWorkflow.Binary.Capnp/Schema/executionReport.capnp
dotnet "$suite" --binary-operation-kinds-tests /tmp/cmwf-e-native/CMakeWorkflow
dotnet "$suite" --source-binding-tests
dotnet "$suite" --binary-workflow-wire-tests /tmp/cmwf-e-native/CMakeWorkflow /tmp/cmwf-e-fault/CMakeWorkflow
dotnet run --project CMakeWorkflow.History.Tests -c Release -p:TreatWarningsAsErrors=true
CMWF_SSHNET_TEST_BINARY=1 bash tests/infrastructure/sshnet/run-tests.sh
```

The wire test optionally takes actual Pre-P4, epoch-2, epoch-3, epoch-4, epoch-5 and epoch-6 executables in that order. The SSH harness uses an actual sshd, managed Clang, explicit vcpkg approval and the preserved schema-3 experiment controller. The SSH experiment inspected the final epoch-7 ledger for a persisted checkpoint and terminal report reference, verified the report blob in CAS, and checked that the remote results contained no `run.json`. The final operation was `0f1d3b899358423baf57688832a3ee1b`. The official Cap’n Proto tool consumes application frames. The framework-dependent interruption build exposes deterministic test hooks. The production build omits those hooks.

The changed projects include the Cap’n Proto contract and code generation check, CMake runner and profiling extension, Host ledger and evidence adapters, Document Store, History, Client artifact catalog projection and CLI artifact CAS. Host fixtures exercise terminal publication, History acknowledgement, source binding and CMake recovery. Production changes and tests remain in the working tree.

## Lightweight measurements

The retained epoch-7 Linux x64 NativeAOT executable is 40,390,728 bytes. Its publish tree with symbols and runtime dependencies is 206,308,741 bytes. The startup and workload observations below used the preceding epoch-7 build. The local startup probe observed 25.858 ms CLI startup, 22.547 ms Host readiness and 29,597,696 bytes of idle RSS. The seven-sample admission probe observed:

| Case | Frame size | Median admission | Median terminal |
| --- | ---: | ---: | ---: |
| Archived epoch-5 JSON path | 842 B | 3.115 ms | 40.301 ms |
| Current JSON path | 845 B | 2.540 ms | 38.349 ms |
| Current native path | 408 B | 5.557 ms | 47.299 ms |

These observations use a local workstation and its then-current load. The native path commits structured checkpoint rows under synchronous FULL. These probes do not isolate or compare equal durability work. Reproduce with `dotnet scripts/measure-persistence-startup.cs CURRENT` and the Host suite option `--measure-binary-workflow CURRENT PREVIOUS`.

## Prerequisites and performance backlog

P4.F still owns cross-store lifecycle coordination, an existing-workspace upgrade protocol, deployment backup and restore, and platform and storage durability verification. The process lifecycle, controller and remaining E.4–E.6 work are P4.E prerequisites. P4.G owns default writer changes and retirement.

P5 should measure checkpoint and event commit costs separately, typed encoding and verification, History import and acknowledgement, large report ranges, peak Host memory and terminal latency. Match the filesystem, synchronous settings, operation graph and evidence set when comparing to the retained baseline.
