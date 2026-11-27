# P4 workflow archive performance and verification

## Baseline and comparable workloads

The historical production checkout is b56f06083a36a67a8d62404e930641b7cbcb4b21. A detached checkout was published before changing production storage. Both builds used .NET SDK 10.0.114, Release, linux-x64 and warnings as errors. The workstation ran CachyOS Linux 7.3.4 on a Ryzen 7 6800H with SDK artifacts and workload data under tmpfs. The source checkout is on NVMe ext4. The combined GC and archive test also ran with an ext4 temporary store. CMake configured the repository's examples/hello tree with Ninja and GCC 16.2.1 for the matrix runs.

Seven process measurements follow two warmups. Every invocation is a new process with ordinary OS caches. The CLI startup case runs tooling catalog. The Host case measures time to its listen announcement. It measures idle CPU over 250 ms after a 50 ms settle. The four planning cases exercise the production CLI and JSON planner against the same configured CMake tree. The 256-node case declares 3,960 ordered edges. This matrix uses inspect nodes to keep build, tool discovery and target availability from changing between revisions.

| Workload | Baseline | Archive-enabled build |
| --- | ---: | ---: |
| CLI catalog, median ms | 11.560 | 12.259 |
| Host to listen, median ms | 9.353 | 9.785 |
| Host idle RSS, bytes | 26,095,616 | 29,159,424 |
| Host idle 250 ms CPU, median ms | 0.000 | 0.000 |
| Host idle threads / descriptors | 4 / 10 | 4 / 10 |
| 4-node plan, 376 B, median ms | 14.248 | 15.088 |
| 65-node plan, 3,670 B, median ms | 15.760 | 15.787 |
| 256-node dense plan, 61,129 B, median ms | 39.556 | 41.392 |
| 512-node plan, 27,808 B, median ms | 21.900 | 22.130 |
| Native executable, bytes | 39,207,016 | 39,405,576 |
| Full publish directory including symbols and documentation, bytes | 202,195,722 | 202,902,756 |

Selected startup and planning workloads were slower in this run and idle RSS was higher. This single-host run does not isolate the causes or establish the variance between build revisions. The archive slice leaves the execution writers in compatibility mode, so the planning and startup cases do not measure binary execution. Both NativeAOT CLIs use glibc, libm, the dynamic loader and the bundled SQLite provider libe_sqlite3.so. SQLite was already bundled in the baseline.

## Persistence and rehearsal observations

A detached baseline Host implementation accepted five legacy workflow operations in a real SQLite ledger, including one with dispatch authorization and four pending operations, using the repository's benchmark example. The new CLI archived all five twice. Retry returned the same descriptors and the ledger was byte-identical after archive. The two shared payloads remained rooted during GC. The detached old Host implementation then read all five operations and verified their original states and snapshots from a restored copy. A separate integration test copied and opened a restored ledger, exercised the two payload representations, ran the actual v2 collector against an action cache root and a mapping held by another process, and verified that the older NativeAOT CLI refused the v2 namespace. No process was dispatched as a consequence of archival.

Linux strace of a fresh archive recorded 82 openat calls, 31 fsync calls, 18 flock calls, and three renames. A retry recorded 57 opens, 12 flock calls, and no fsync or rename in that trace filter. The fresh case combines catalog initialization, version-marker publication, both blobs and metadata. The trace uses tmpfs and does not establish the costs of disk flushes. The P3 ext4 report gives storage-specific measurements under the earlier codec and durability policy. [P3 measurements](capnp-p3-performance.md)

Host contract, recovery, cancellation, approval, history, tooling, workflow integration and P0–P3 suites were exercised separately from the timing runs. Official Cap’n Proto C++ accepted the production frame and verified the canonical digest of the populated fixture. The native CLI vintage test and archive execute on Linux x64 with warnings as errors. Windows archive execution is configured in CI and has no local result. Fault injection verifies catalog rollback, orphan collection and retry after a failed archive commit. This does not establish power-loss behavior or execution rollback for binary-only operations.

## Reproduction and remaining measurements

    git worktree add --detach /tmp/cmwf-baseline b56f06083a36a67a8d62404e930641b7cbcb4b21
    dotnet publish /tmp/cmwf-baseline/CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -o /tmp/cmwf-baseline-native
    dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -warnaserror -o /tmp/cmwf-current-native
    cmake -S examples/hello -B /tmp/cmwf-plan-build -G Ninja
    dotnet run --file scripts/measure-persistence-startup.cs -- /tmp/cmwf-baseline-native/CMakeWorkflow /tmp/cmwf-plan-build
    dotnet run --file scripts/measure-persistence-startup.cs -- /tmp/cmwf-current-native/CMakeWorkflow /tmp/cmwf-plan-build
    dotnet run --project CMakeWorkflow.Host.Tests -c Release -p:TreatWarningsAsErrors=true -- --workflow-archive-tests /tmp/cmwf-current-native/CMakeWorkflow /tmp/cmwf-baseline-native/CMakeWorkflow

The system-wide before/after comparison remains incomplete. Managed allocation and GC profiles, detailed admission and plan persistence latency, managed environment and dependency flows, remote execution, cache and history throughput, recovery, cancellation latency and repeated inspection need comparable production workloads after writer cutover. Procedure-level correctness runs do not substitute for those measurements.

## P5 candidates supported by this slice

- Separate Host startup working-set changes from page-cache and binary layout variability using repeated matched launches. Compare the executable growth with a capture of resident mappings.
- Split catalog initialization and CAS synchronization from per-document cost. The fresh/retry traces differ materially in fsync and file opens.
- Measure repeat archive cost under contention. The retry reuses existing physical payloads and verifies their integrity while holding the namespace lease.
- Profile dense planning independently of the CLI startup and JSON output. The dense 256-node fixture took more time than the 512-node sparse fixture.
- Evaluate map versus buffered access and projection after actual workflow writer cutover. The P3 experiment isolated those modes and found both benefit from reusing a verified owner.
