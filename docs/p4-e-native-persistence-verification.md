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
  
  
  ## Epoch 8 process execution and historical Action Cache import
  
  Fresh opt-in binary workspaces use epoch 8. General process operations keep their admitted input snapshot and existing results reservations in the operation ledger. The process runner advances one canonical checkpoint generation and its public event cursor in one SQLite transaction. Dispatch confirmation binds the exact frame digest and inserts per-node launch intents before the scheduler may call the OS. Independent observations record child start, the PID paired with the OS start timestamp when available, observed child completion, termination request, termination confirmation, or uncertainty.
  
  A launch intent establishes possible external effects. The process may start before the subsequent lifecycle append reaches disk. Process recovery still requires explicit reconciliation and a permanent resume claim. It rejects any eligible retry candidate with a prior launch intent. A lifecycle observation alone grants no retry authority. Linux termination confirmation records supervised root-process exit following a process-tree kill request. It cannot attest to independently detached descendants.
  
  The finalized process frame is an immutable, hash-checked SQLite row committed with the terminal transition. Its stored frame digest identifies the native evidence. The existing public report SHA-256 identifies the projected JSON. The artifact API provides that same JSON on demand and exposes the exact native frame as `native-report`. Event cursors continue through the existing replay API. Older ledgers retain their original authority and digest policies.
  
  For Action Cache, `CMakeWorkflow cas DEST init-indexed` creates the v3 destination. Then `CMakeWorkflow cas DEST import-actions LEGACY --key-policy 1` imports entries from a compatible legacy CAS. Use policy 2 for entries produced under policy 2. Legacy JSON does not specify the key policy, so choose the policy of the producing workload. Imported content must verify against its original physical digest and size. The source is preserved. The destination publishes verified blobs before committing the batch of metadata rows, and equivalent repeated imports are idempotent. GC can reclaim blobs left by an interrupted import before its SQLite commit.
  
  
  `experiment plan --native-controller` produces an immutable schema-4 `plan.capnp`. Its approval hash uses the `cmwf-experiment-controller-approval-v2` domain and covers the exact canonical frame. The plan binds the Git commit and snapshot, ZIP descriptor, selected Host and optional placement, dependency digest, and retained workflow descriptor. The workflow referenced by that descriptor carries the approved environment policy. The controller revalidates the plan hash and opens the retained workflow before staging. `experiment inspect ID` renders the verified plan as JSON. Existing `--binary` plans retain schema 3 and its approval domain.
  
  The existing pipeline ledger records native controller and dependency approvals. The controller can reopen an approved pending stage after an interrupted upload, and the existing child coordinator resolves persistent dispatch intents by Host request ID. Source ZIP and cached result exports retain their public formats. Controller workflow retention uses the existing Document Store lease and immutable content publication.
  
  `script run-binary FILE.cs --approve` uses the existing script response protocol, then encodes and stages a native frame without publishing a legacy workflow manifest. Initialize the execution workspace with `workflow init-binary` before using this opt-in path.
  
  ### Epoch 8 verification summary
  
  This increment started at `f37e06fc0aa04f6fd96b433d485c04b3d3550034`. The archived binaries remain in the directories listed above. The initial untracked user files were `graphify-out/`, `poc/` and `usings.txt`. The working tree contains the implementation and verification changes without a new commit.
  
  | Slice | Status | Production verification and remaining limits |
  | --- | --- | --- |
  | P4.E.0 | Complete and verified | Updated the [persistence inventory](persistence-inventory.md) for process state, Action Cache import, controller plans and script publication. |
  | P4.E.1 | Partial | CMake retains typed terminal, benchmark and capture evidence and versioned artifact manifests. Production fault tests verified retained publication, finalization, missing and corrupt evidence, and official encoding. Supplemental derived profile analysis still uses JSON sidecars. Process terminal evidence currently commits as an immutable frame in the operation ledger. |
  | P4.E.2 | Implemented with outstanding verification | Native History directly materializes CMake terminal frames and has mixed-format, interrupted commit, retry and integrity coverage. Profiling supplements still consume derived JSON. |
  | P4.E.3 | Complete and verified | Epoch 8 process tests covered checkpoint and event publication, launch intent and OS observation boundaries, SIGKILL, cancellation, reconciliation, a one-shot resume, source deletion and public artifacts. Prior ledger readers and epoch-7 fencing were verified with archived executables. |
  | P4.E.4 | Complete and verified | Imported an actual cached output from the archived epoch-3 producer. Verified interrupted import before metadata commit, collection, parallel equivalent imports, conflicting policy refusal, reuse, missing and corrupt blobs, and eviction. |
  | P4.E.5 | Implemented with outstanding verification | A schema-4 controller plan was executed via real SSH with explicit dependency approval, managed Clang, a vcpkg receipt, Host restart, CTest, three benchmark trials, History and verified artifact download. Verified plan tampering, refusal of the ordinary SHA-256 in place of its format-specific approval, and official Cap’n Proto canonical bytes. The controller was also killed after staging and restarted by the report command using its SQLite approval. The script native admission smoke ran through its configured CMake operation. |
  | P4.E.6 | Partial | The existing process JSON report and event API work over native authority, and the exact native frame can be downloaded. The controller exposes a JSON inspection projection. Derived profiling evidence remains the native-path serialization dependency. |
  | P4.E.INTEGRATION | Implemented with outstanding verification | All nine CMake operation kinds passed through the Linux x64 NativeAOT executable. Native CMake and process recovery, legacy recovery, archived executable fencing, report publication, History retry, cache import and actual SSH passed. The full requested fault and performance matrices remain open. |
  
  See [process lifecycle and checkpoints](adr/0015-native-process-lifecycle.md) and [experiment controller plans](adr/0016-native-experiment-controller.md) for transaction boundaries, identities and ownership. The mutable operation ledger uses SQLite DELETE journal mode and FULL synchronous writes. The pipeline ledger uses WAL and FULL synchronous writes. Action Cache references use the existing SQLite index and namespace lease. A native process checkpoint generation and event cursor share one transaction. Process launch and its later OS observation have separate durability boundaries.
  
  ### Reproduction
  
  Run from the repository root with .NET 10, CMake, Ninja, the official Cap’n Proto 1.2.0 executable and the archived runtimes. Linux fault fixtures require the separate interruption build.
  
  ```sh
  dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -o /tmp/cmwf-native
  dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror \
    -p:PublishAot=false -p:RecoveryFaultInjection=true -o /tmp/cmwf-fault
  dotnet build CMakeWorkflow.Host.Tests -c Release -warnaserror -p:RecoveryFaultInjection=true
  suite=CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll
  dotnet "$suite" --native-process-tests /absolute/capnp CMakeWorkflow.Binary.Capnp/Schema/processEvidence.capnp
  dotnet "$suite" --process-recovery-tests
  dotnet "$suite" --binary-operation-kinds-tests /tmp/cmwf-native/CMakeWorkflow
  CMWF_TEST_NATIVE_HISTORY=1 dotnet "$suite" --native-report-tests \
    /tmp/cmwf-native/CMakeWorkflow /tmp/cmwf-fault/CMakeWorkflow /absolute/capnp \
    CMakeWorkflow.Binary.Capnp/Schema/executionReport.capnp
  dotnet "$suite" --import-action-cache-tests /tmp/cmwf-native/CMakeWorkflow \
    /tmp/cmwf-fault/CMakeWorkflow out/p4-ef-verification.zNU9Md/epoch3/CMakeWorkflow
  CMWF_SSHNET_TEST_BINARY=1 bash tests/infrastructure/sshnet/run-tests.sh
  ```
  
  For the optional historical wire matrix, pass the archived executables in order after the current and fault executables. The supported test arguments end with epoch 7. The fixtures verify the previous stores as well as refusal to mutate the fresh store.
  
  The remaining P4.E prerequisites for P4.F are the supplemental native profiling and History evidence contract and the outstanding fault and projection verification. P4.F still owns cross-store retention, deployment recovery, backup and upgrade policy. P5 can measure process checkpoint encoding and SQLite write amplification, per-attempt lifecycle observation cost, report and History publication latency, and cache import and lookup costs under identical durability settings.
  
  
  The repeated schema-4 SSH run recovered operation `4e470ad07fb34cc0afd857b17a6ac6bc` after a controller interruption and then a Host interruption. It verified vcpkg receipt `659d3f378619c87e1eecf848f473889e095b0ea1abd24617cefbbb3a825165a7`. The controller process ran the official Cap’n Proto 1.2.0 canonical byte comparison against the actual approved frame. The ordinary Host, History, pipeline coordinator, managed Cap’n Proto, code generation and Document Store suites passed.
  
  The epoch-8 NativeAOT publish sampled by `dotnet run --file scripts/measure-persistence-startup.cs -- /absolute/CMakeWorkflow` was 40,506,776 bytes. Its publish directory held 206,624,697 bytes of files, including symbols and runtime dependencies. Median catalog startup was 14.720 ms, median Host endpoint readiness was 14.819 ms, and median idle RSS was 29,487,104 bytes across seven samples after two warm-ups. These are workstation observations. Separate transaction cost, peak RSS and terminal latency measurements remain part of the open verification work.
  
  The final Linux x64 NativeAOT executable has SHA-256 `5d6919fea0fdcf161e6a3b9e6f5aeda4453e7e77b50163c50496a3ec341fe50f` and a 206,624,489-byte publish directory. The final executable passed all nine CMake operation kinds and a separate process-domain SDK run that verified SQLite events, the JSON projection, and the downloadable native artifact. The official Cap’n Proto canonical segment matched that artifact byte for byte. The real SSH schema-3 compatibility run and native source-artifact case also passed.
  
  ## Epoch 9 acceptance update
  
  The [native profile analysis ADR](adr/0017-native-profile-analysis.md) records the remaining serialization change. Fresh binary ledgers bind typed perf and ETW analysis in the canonical terminal report. History uses that evidence directly. Earlier ledgers keep the existing JSON-based enrichment path. Public capture files and explicit profile analysis exports remain available.
  
  The [process lifecycle ADR](adr/0015-native-process-lifecycle.md#exact-retry-at-the-terminal-boundary) now covers the OS start and exit observation windows, surviving children and retry before and after the terminal transaction. Epoch 9 stores the Linux boot ID and kernel start counter with the child PID. The terminal checkpoint can finish the original transaction on an exact retry, even after the submission file has been removed.
  
  The final Linux verification uses strict Release builds, the Linux x64 NativeAOT executable and a separate framework-dependent fault build. The nine CMake operation kinds and the process SDK smoke ran on the production executable. The archived epoch-8 executable refused the epoch-9 ledger. The current executable could inspect and replay the earlier ledger. The schema-4 SSH experiment interrupted both the controller and Host, resumed operation `1814fc00937549b5be8834d915ee3800`, and verified vcpkg receipt `f26c7e24d74c3fb7ffbc8920dd9ce4fcd9e838dfdc7fc41cedd32fc2ed19d03a`. The experiment ran CTest and benchmark trials, indexed History and downloaded a verified artifact. The native History regression exercised mixed evidence, missing terminal documents, interrupted acknowledgement and retry. The profiling regression used the actual Linux perf backend and compared its retained native report against the official canonical encoding.
  
  ### Additional automated checks
  
  After the native and fault publishes described above, build the Host suite with fault injection enabled. These commands use disposable stores and workloads.
  
  ```sh
  CMWF_TEST_CAPNP=/absolute/capnp CMWF_TEST_PROFILE_SCHEMA=CMakeWorkflow.Binary.Capnp/Schema/executionReport.capnp \
    dotnet "$suite" --native-profile-history-tests /tmp/cmwf-native/CMakeWorkflow /tmp/cmwf-fault/CMakeWorkflow
  dotnet "$suite" --native-script-recovery-tests /tmp/cmwf-native/CMakeWorkflow /tmp/cmwf-fault/CMakeWorkflow \
    CMakeWorkflow.Scripting/CMakeWorkflow.Scripting.csproj
  dotnet "$suite" --measure-binary-workflow /tmp/cmwf-native/CMakeWorkflow \
    out/p4-ef-verification.zNU9Md/epoch3/CMakeWorkflow
  dotnet "$suite" --measure-process-persistence "$PWD/out"
  dotnet "$suite" --measure-cache-persistence "$PWD/out"
  ```
  
  ### Matched-workload observations
  
  The current legacy and native workflow cases used the same managed test application and an already configured Ninja tree with one `inspect` node, one job and an independent History database per case. The workflow and History latency probes used tmpfs and timed from RPC submission. Codec medians used 19 warmed managed samples. Runtime medians used seven warmed samples with 30 ms completion polling. Ledger and CAS sizes were measured after nine operations, excluding History, which is shown separately. The historical epoch-3 reference case did not include explicit indexing.
  
  | Current writer | Manifest bytes | Managed encode / decode | Codec allocation | Host peak RSS | Ledger and CAS | Admission / terminal / indexed from submit | History DB |
  | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
  | Legacy | 845 | 7.1 / 17.8 µs | 3,152 B | 62,693,376 B | 151,552 B | 2.086 / 37.477 / 39.340 ms | 65,536 B |
  | Native | 408 | 16.1 / 14.1 µs | 4,840 B | 64,925,696 B | 336,093 B | 4.843 / 40.150 / 42.027 ms | 57,344 B |
  
  The process and cache probes used ext4 under `out`. The process benchmark used an identical two-node graph. The checkpoint sample measured encoding plus a durable write or a bound read plus decoding. The 21 warmed write/read samples omit event rows. The seven recovery samples include explicit admission, the pending child and finalization. The native checkpoints use SQLite synchronous FULL and DELETE journal mode.
  
  | Process writer | Checkpoint bytes | Durable write | Read and decode | Recovery with pending child |
  | --- | ---: | ---: | ---: | ---: |
  | Legacy | 518 | 15.877 ms | 0.369 ms | 380.297 ms |
  | Native | 720 | 26.196 ms | 0.317 ms | 445.006 ms |
  
  The cache benchmark restored the same 1 MiB blob and measured the verified restore through the normal lookup, hash and durable copy path. Medians from 11 warmed samples were 18.035 ms for legacy JSON and 25.488 ms for SQLite. One import took 63.210 ms. These measurements include workstation load and the work performed by each persistence policy. They do not establish a speedup or separate the SQLite commit from encoding and verification.
  
  ### Remaining verification
  
  Windows live ETW and macOS Instruments could not run on the Linux workstation. Linux exercised perf and the ETW fixture parsers. The exhaustive cancellation, transport interruption and fault combinations remain wider than the tested boundaries. The process terminal frame continues to be an immutable ledger row with atomic finalization. This increment adds no existing-workspace upgrade, default writer cutover or P4.F deployment policy.
  
  The epoch-9 executable SHA-256 is `d2b24b6a7184f9697a23ed7986bb3053694747da0665bf14253162506bd341b7`. The executable is 40,581,752 bytes and the publish directory is 206,953,972 bytes. The strict Host build reported zero warnings and errors. The full Host and CLI integration suites, History suite and profiling backend suite passed. The additional process fixture stopped the Host before launch, before recording child start and exit, and before and after terminal finalization. It also simulated concurrent resume claims and tested refusal to dispatch while an identified child survived. The script fixture killed the controller after terminal report publication and verified one external effect across two exact retries. The profiling fixture restarted with removed and invalid compatibility sidecars, refused a corrupted native report, and then indexed the restored perf evidence from both captures.
  
  The implementation plan now treats the Linux native profiling path and representative lifecycle and reindexing cases as verified. Strict Release, NativeAOT, real SSH, current and historical wire readers, and the controlled cache import cases have verified results. P4.E as a whole remains open for transport-level reply loss across the full controller sequence, the exhaustive fault matrix, and the unexecuted live platform cases. The SSH interruption test loses the controller after staging. It does not drop the upload packet itself. The coordinator's lost-start-reply test runs at the SDK coordinator boundary. No status for P4.F or P4.G changes here.
