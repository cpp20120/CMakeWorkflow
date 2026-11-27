# Native workflow execution verification and measurements

## Starting state

The repository starts at b56f06083a36a67a8d62404e930641b7cbcb4b21 with P4.A present as working-tree changes. A source tree archive and the existing independently built Linux NativeAOT P4.A executable were saved before editing. P4.A's Host regression suite was run at that point. No commit was created for the final working tree. The existing P4.A report remains unchanged. [P4.A baseline](p4-persistence-performance.md)

## Verified behavior

- A fault-enabled Host subprocess admitted a frame originating from a typed WorkflowDocument with no persisted legacy workflow input. The test removed the staging frame and tested an identical retry, an altered request and a legacy retry under the same request ID. Only the original operation remained admitted.
- The subprocess was killed after the write-ahead binding for node B and before B launched. A previously completed A effect was present exactly once. A separate Host reopened the operation, recovered the same request ID, verified the retained native frame, enforced reconciliation and resume admission and executed only B. The test checked the declared output in the verified artifact catalog, the terminal report in history and the empty legacy snapshot column.
- A wrong checkpoint digest and an altered dispatch authorization identity were refused before resume. A valid approval/recovery transition was still usable after restoring the genuine binding. The real v2 collector retained the binary operation input after the crash and after finalization.
- The actual P4.A NativeAOT Host was refused when it tried to admit a legacy operation into the fenced pathname. Its v2 collector was able to collect the new CAS without deleting the retained input. Previously verified pre-v2 collectors continue to refuse the v2 CAS marker.
- Linux x64 NativeAOT published with warnings as errors and executed binary admission, resume, JSON projection, CLI inspection, recovery and GC. The final subprocess proof used a temporary ext4 workspace. Native script compose-binary produced a frame the official frontend decoded. The killed participant was built with the repository's existing managed fault-injection configuration. The restarted production participant was NativeAOT. Windows execution is wired in CI and was unavailable locally.
- The official C++ oracle verified the workflow's canonical bytes and the new dispatch binding's canonical segment. The official frontend decoded the binding under the generated DispatchBinding schema. Compiler output checks and the P0–P4.A wire suites passed.
- The production Host, workflow integration, contracts, tooling, provisioning, History, archive and legacy source-bound suites passed locally. The source-bound suite uses the linux-x64 reference runtime for its native Lore dependency.

The fault run covers one deterministic after-checkpoint boundary. It does not establish exactly-once external effects, arbitrary multi-node recovery, all crash windows, or power-loss guarantees. The binary source-bound and remote experiment flows still need approval and source-specific integration tests.

## Comparable local workloads

Linux x64, .NET 10.0.114 and the same local platform as P4.A. Runtime test state was under tmpfs. The host executes the same one-node observe-mode CMake workflow using the same RPC transport, planner, terminal report and configured fixture structure. Nine operations per case discard the first two. Startup and fixture creation are outside these operation timings. Binary runs reuse one physical CAS document for the repeated input. Admission includes validation and retention for binary input, while legacy planning begins after the durable reply. Terminal latency includes the same 30 ms status polling cadence across cases.

| Production Host case | Input bytes | Admission median | Terminal observed median |
| --- | ---: | ---: | ---: |
| P4.A Host, legacy input | 842 | 1.248 ms | 35.119 ms |
| Current Host, legacy input | 845 | 1.258 ms | 34.979 ms |
| Current Host, native binary input | 408 | 4.050 ms | 37.620 ms |

Input sizes include the different absolute fixture paths. Startup data collected during the change recorded a Host median of 11.99 ms and idle RSS of 27.95 MiB, versus 9.79 ms and 27.81 MiB in the prior P4.A report. The runs were on the same machine at different times and are not an isolated startup regression measurement. The current native executable was about 39.56 MB versus 39.41 MB for P4.A. The publish directory including debug symbols was about 203.50 MB versus 202.90 MB. The CLI and Host still use the bundled native SQLite and Lore providers for their existing storage and source capabilities.

    dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -o out/binary-native
    dotnet publish CMakeWorkflow.csproj -c Release -p:PublishAot=false -p:RecoveryFaultInjection=true -warnaserror --artifacts-path out/fault-build -o out/fault
    dotnet run --project CMakeWorkflow.Host.Tests -c Release -- --binary-workflow-wire-tests out/binary-native/CMakeWorkflow out/fault/CMakeWorkflow /absolute/path/to/p4a/CMakeWorkflow
    dotnet run --project CMakeWorkflow.Host.Tests -c Release -- --measure-binary-workflow out/binary-native/CMakeWorkflow /absolute/path/to/p4a/CMakeWorkflow

P5 priorities are repeated full document verification, the admission inspection pass, CAS lock wait time, and the additional authorization binding and checkpoint transactions. Broader measurements for remote execution, allocation and GC, managed environment or dependency provisioning, and durable recovery on deployment filesystems remain outstanding. [Execution architecture and remaining P4 slices](adr/0009-binary-workflow-execution.md)

## P4.C and P4.D follow-on

The follow-on started at `b56f06083a36a67a8d62404e930641b7cbcb4b21` with earlier P0–P4 work present locally. It adds opt-in production readers, writers and tests in the working tree. No commit, default switch or in-place workspace migration was performed. The final status had 61 tracked working-tree entries and 50 untracked entries, including earlier P0–P4 work. The sections above record the earlier epoch-2 work and its measurements.

| Slice | Status | Production path and verification |
| --- | --- | --- |
| C1 Operation coverage | Complete and verified | The Linux workflow differential executes all nine kinds over Host RPC in both formats. The fixture runs a selected dependency closure with parallel scheduling, real Clang formatting, CMake, CTest and both perf captures. |
| C2 Source-bound workflows | Complete and verified | The native Host admits the verified Lore identity and directly plans from a typed manifest. Tests cover live checkout changes, missing Lore data, altered materialization, conflicting source identity and actual pinned Git experiment recovery. |
| C3 Remote binary execution | Complete and verified | The SDK and `remote start-binary` use existing local, OpenSSH and SSH.NET transports. Live verification used SSH.NET against real OpenSSH `sshd`, removed the staged frame and resumed on a replacement Host. Managed experiment staging uses the existing resumable source protocol. |
| D1 Execution plans | Complete and verified | Fresh execution ledgers authorize a retained `ExecutionPlanCapnp` document and resolve against the exact committed binding during recovery. Existing workflow declarations, generated runtime observations and mutable journal have separate ownership. Local fault tests reject missing plans and altered authorization. |
| D2 Environment locks and tooling plans | Implemented with outstanding verification | Host discovery and Linux toolchain resolution read native locks. Tooling plan and setup authorize exact frames with the existing policy and consent. The real remote test reused preinstalled releases and verified them. A fresh installation and interruption during binary tooling setup have not been exercised. |
| D3 Dependency plans and receipts | Complete and verified for the managed Linux vcpkg path | A real vcpkg run installed `cxxopts` using an explicit Git baseline and triplets. Native approval binds the dependency plan. The first receipt is retained and checked against the live package installation. |
| D4 Approval and dispatch | Complete and verified for the migrated workflow and managed Linux paths | Native identity policies cover exact reviewed plans, source, environment, dependencies and receipt. Dispatch and recovery bind those inputs without transferring approval from the legacy format. |
| CD1 Native remote experiment | Complete and verified on Linux x64 | The production CLI planned from a Git commit, applied a native dependency approval, started the native workflow, and observed recovery after SIGKILL. The experiment rebuilt from the captured source, completed CTest and three benchmark trials, downloaded a verified artifact and indexed History. |
| CD2 Verification and faults | Partial | Linux NativeAOT, official Cap’n Proto interoperability, binary source/SSH recovery, corrupt or missing plan refusal, and current Host and tooling regressions passed. Broader fault/concurrency matrix, cache variants and Windows/macOS execution coverage remain. |

### Operation coverage

| Kind | Native production evidence |
| --- | --- |
| inspect | Inspected the generated CMake File API tree. |
| configure | Generated a fresh Ninja build. Also ran in the operation-owned Lore tree over SSH. |
| build | Verified the built executable and recorded its artifact. |
| invoke | Invoked a declared target. Local and SSH fault fixtures resumed a single interrupted target. |
| run | Ran the built binary. The SSH experiment resumed three ordered benchmark trials. |
| profile | Ran real Linux perf counters against the built executable. |
| profile_snapshot | Verified a pinned executable SHA-256 and ran a separate perf counters capture. |
| ctest | Ran the declared test against the built executable, including the SSH vcpkg consumer. |
| format_check | Ran the production C++ formatting check with a real clang-format process. |

The managed SSH fixture verifies that there are no legacy workflow, environment lock or provisioning receipt files for the operation. It inspects the copied operation ledger for a zero-length legacy snapshot, binary input, policy 2 and a retained plan DocumentId. Dependency approval is recomputed from the binary plan. The experiment RPC can render a JSON dependency view for people to review, and the controller stores public metadata and the binary frame in its existing JSON envelope. Runtime checkpoints, CMake files, `vcpkg.json`, reports and History evidence use their respective existing protocols.

The final interrupted experiment used plan `A19C2071DBD9ADC46DDB57064B0B6DE771FD7F21FC17C2A8A266E228A967F4D6`, operation `6104ca19984948b2a78c32e997edfa90` and binary receipt `6d1d63eb4cb810f07dfa59270fcb126e3da3bad1b6cf19246ec19e947dc38ea1`. The fault-enabled process stopped after the second trial’s write-ahead checkpoint. Its process terminated and its staging frame was removed before CAS collection. The restarted NativeAOT Host found the original request, verified the retained input and plan, and resumed after an explicit decision. The first trial’s observed side effect did not repeat. This evidence covers the injected pre-launch boundary.

The remote test refused a legacy approval over the same plan file, a wrong dependency approval, an absent dependency installation, an altered native lock and an altered native receipt. Two simultaneous approvals for the same plan returned the same first receipt. The local wire test raced two admissions with the same request ID over independent connections, ran GC while the Host held a checkpoint, then refused altered authorization and a missing plan. An actual earlier inline-binding binary could read and write its own epoch-2 workspace and was fenced from epoch 3. The current executable could inspect the earlier inline workspace. Historical P4.A execution evidence is recorded above.

The Action Cache test reused a verified legacy result from an equivalent native invocation. An unrelated workflow timeout change left the action key unchanged. Changed effective arguments, environment and declared input bytes each produced a miss. Corrupted and missing cached blobs failed without an external invocation. Native managed fingerprints also incorporate the verified binary vcpkg receipt identity. The cache metadata format and legacy key preimage retain their existing behavior.

The official Cap’n Proto 1.2 compiler reproduced the generated workflow, execution-binding and tooling schema readers. The official CLI decoded actual native execution plan, environment lock, tooling plan and provisioning receipt frames and recovered their canonical segments by independently encoding the decoded values. The C++ workflow oracle and Document Store fault and retention suites passed. The full Release solution built with zero warnings and errors. Existing Host, CMake integration, Scripting, Project SDK, tooling, contracts, pipeline coordinator and History checks passed.

The execution CAS roots the workflow and plan. The existing managed tool store retains environment locks, pinned approval evidence and installed receipts, and the existing Lore store retains source. No common collector currently governs the lifetime of all three stores. A source-bound workflow currently refuses manifest-rooted inputs and outputs, including manifest-rooted Action Cache workloads, because the remapper cannot safely bind those paths to the captured snapshot.

### Reproduce

Build and run the typed Linux verification harness. Its normal source checks use the Linux Lore native runtime.

```sh
work=$(mktemp -d)
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -o "$work/native"
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror \
  -p:PublishAot=false -p:RecoveryFaultInjection=true --artifacts-path "$work/fault-build" -o "$work/fault"
dotnet build CMakeWorkflow.Host.Tests -c Release -r linux-x64 -warnaserror
suite=CMakeWorkflow.Host.Tests/bin/Release/net10.0/linux-x64/CMakeWorkflow.Host.Tests.dll
dotnet "$suite" --binary-workflow-wire-tests "$work/native/CMakeWorkflow" "$work/fault/CMakeWorkflow"
dotnet "$suite" --binary-operation-kinds-tests "$work/native/CMakeWorkflow"
dotnet "$suite" --binary-action-cache-tests "$work/native/CMakeWorkflow"
dotnet "$suite" --source-binding-tests
CMWF_SSHNET_TEST_BINARY=1 bash tests/infrastructure/sshnet/run-tests.sh
```

The wire test accepts optional executable arguments for the archived P4.A and inline P4.B releases, in that order. The SSH fixture uses an isolated loopback OpenSSH instance, an ephemeral pinned key and real managed installations. The nine-kind test requires Linux perf counters and clang-format.

These commands show the flow the SSH verification drives. Run the first three commands on the remote Host. Obtain `LOCK_ID` from the verified resolve output, and review the exact provisioning plan before passing its digest to setup if installation is needed.

```sh
CMakeWorkflow workflow init-binary "$WORKSPACE"
CMakeWorkflow tooling resolve --binary --profile linux-clang --generator 'Ninja Multi-Config' --json
CMakeWorkflow tooling lock --binary --profile linux-clang --generator 'Ninja Multi-Config' --approve-sha "$LOCK_ID"

CMakeWorkflow experiment plan --repo "$REPO" --ref "$COMMIT" --host "$SSH_HOST" \
  --workspace "$WORKSPACE" --endpoint "$REMOTE_SOCKET" --user "$SSH_USER" \
  --key-file "$PRIVATE_KEY" --host-key-sha256 "$HOST_PIN" --remote-executable "$REMOTE_CLI" \
  --managed-clang --config Release --binary --environment-lock "$LOCK_ID" \
  --vcpkg --benchmark-target benchmark --repeats 3
CMakeWorkflow experiment deps plan "$PLAN_ID"
CMakeWorkflow experiment deps apply "$PLAN_ID" --approve --plan-sha "$DEPENDENCY_ID"
CMakeWorkflow experiment run --plan "$PLAN_ID" --approve
CMakeWorkflow experiment report "$PLAN_ID"
```

Recovery uses the operation handle’s `InspectRecoveryAsync`, `ReconcileBinaryRetryAsync`, `AdmitResumeAsync` and `ExecuteResumeAsync` in that order. The CLI’s report command observes the original controller operation after reconnecting.

### Lightweight follow-on measurements

Linux x64, CachyOS kernel 7.3.4 and glibc 2.44, on an active workstation. Seven measured runs per case used the same local transport and one-node CMake observe workload. These measurements include different amounts of durable work and set no acceptance threshold.

| Case | Frame bytes | Median admission | Median observed terminal |
| --- | ---: | ---: | ---: |
| Current legacy | 845 | 1.396 ms | 36.342 ms |
| Current native | 408 | 4.446 ms | 39.386 ms |

The Linux NativeAOT executable measured 39.77 MB and the full publish tree including debug symbols measured 204.19 MB. The existing startup harness observed a 12.81 ms median to the listening endpoint and 28.0 MiB median idle RSS. These are workload observations from the same workstation. Peak RSS and isolated publication, receipt verification and remote recovery timings remain open. Reproduce the admission comparison with `--measure-binary-workflow CURRENT PREVIOUS` and startup with `dotnet scripts/measure-persistence-startup.cs CURRENT`.

### Remaining backlog

- P4.E covers report, History evidence, journal and general Action Cache metadata persistence. It also owns verification for historical projections and public readers.
- P4.F covers the Windows and macOS execution matrix, power loss and deployment filesystem semantics, managed provisioning interruption, and broader mixed-version and GC testing.
- P4.G covers writer defaults, operator-managed retention across execution and tooling stores, rollback operations and legacy retirement policy.
- P5 covers isolated admission, plan publication, approval and receipt verification, remote recovery, RSS and throughput measurements. The current portable evidence establishes functional behavior rather than performance thresholds.
