# CMakeWorkflow

CMakeWorkflow is a standalone .NET 10 CLI for orchestrating work in CMake
projects. It plans explicit workflows, asks CMake to configure and build native
targets, runs executables and CTest, and records results. It works with existing
build trees and does not require a particular C++ library, repository layout, or
CMake toolkit.

CMake and its generator remain responsible for native dependencies, compiler
flags, incremental builds, presets and test registration. CMakeWorkflow owns the
workflow graph, process lifecycle, build-tree coordination, provenance checks
and run artifacts.

## Documentation

The [documentation index](docs/README.md) describes the current implementation:
architecture, workflow manifests, CLI commands, result protocols, History,
profiling, provisioning and IDE integration. Documentation is currently in Russian.
The proposed MSBuild execution backend is discussed separately in
[ADR-0001](docs/adr/0001-msbuild-execution-backend.md); it is not implemented.

The [local Host IPC preview](docs/host-ipc.md) exposes the existing Doctor and explicit Fix services
to CLI and VS Code clients over Unix sockets / Windows named pipes, with
NativeAOT-compatible JSON-RPC contracts. See [ADR-0002](docs/adr/0002-local-host-ipc.md)
for scope and migration boundaries. Fix reuses the existing installer; workflow
execution continues to use its existing CLI commands.

## Build and try it

Requirements: .NET 10 SDK and CMake 3.26 or newer. The included example and
integration tests also use Ninja and a C++ compiler.

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --results-dir out/results
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow report out/results/<run-directory>
```

Publish a native executable with the RID for your host, for example:

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
out/native/CMakeWorkflow workflow plan examples/hello/workflow.json
```

The example is a small managed workflow: configure, build, execute and run
CTest. For an existing IDE tree, use `mode: "adopt"` and
`provenancePolicy: "allowUnverified"`, omit the configure node and point
`buildDirectory` at that tree.

## Commands

```sh
CMakeWorkflow context inspect BUILD_DIR
CMakeWorkflow target build --build-dir BUILD_DIR --target TARGET
CMakeWorkflow target run --build-dir BUILD_DIR --target EXECUTABLE -- ARG...
CMakeWorkflow target invoke --build-dir BUILD_DIR --target CUSTOM_TARGET
CMakeWorkflow target resolve --build-dir BUILD_DIR --target EXECUTABLE
CMakeWorkflow workflow plan FILE
CMakeWorkflow workflow run FILE --results-dir DIR
CMakeWorkflow workflow report RUN_DIRECTORY
```

`workflow plan` validates the selected graph and reads existing build-tree
data without building. `workflow run` executes its dependency closure, retains
logs and writes a `run.json` journal. `target build` requests a known native
target; `target invoke` intentionally executes a custom or aggregate target.
`target resolve` only reads the CMake File API codemodel.

## Workflow contract

Schema version 2 supports `inspect`, `configure`, `build`, `invoke`, `run`,
`profile` and `ctest` nodes, dependency closure, resource constraints,
timeouts and cancellation. Schema version 1 remains accepted. Managed contexts
require an explicit configure node. Adopted contexts use an existing tree
without configure provenance. Strict and `allowUnverified` policies control
whether execution may continue after provenance becomes uncertain. Known
source, generator and configuration conflicts always fail.

Every build request reaches CMake's backend. A dependent `run` node executes an
artifact from a successful build node and checks its recorded identity. An
existing executable file does not prove its target is current. The CLI never
infers a configure preset from cache entries or starts configure, clean or
rebuild implicitly.

CMakeWorkflow keeps configure provenance and optional bridge manifests under
`cmake-workflow/` in each build tree. Its leases coordinate only other
CMakeWorkflow processes. An IDE or independent CMake invocation can still
change that tree. File API generation changes can downgrade provenance even
when observed configuration properties remain equivalent; neither a successful
build nor a recorded configure invocation proves a hermetic environment.

The optional [CMake target bridge](cmake/CMakeWorkflowTargetBridge.cmake) lets a
custom target invoke a prepared executable without recursively entering its
own build tree. Set `WORKFLOW_RUNNER_EXECUTABLE` to the published CLI before
configuring a project that uses the adapter. The CLI itself works without the
adapter; CMake File API data is its normal target source.

## Projects and checks

- `CMakeWorkflow.Core/` provides process execution, cancellation, hashing and
  generic operation contracts without CMake or project-specific dependencies.
- `CMakeWorkflow.CMake/` handles File API inspection, targets, build-tree
  ownership and the workflow graph.
- `CMakeWorkflow.Profiling/` provides platform capture and analysis operations.
- `CMakeWorkflow.Campaigns/` provides optional project-specific campaigns.
- `CMakeWorkflow.History/` indexes completed run artifacts in local SQLite
  for later queries and descriptive comparisons.
- `CMakeWorkflow.Tooling/` provides optional tool discovery, preflight plans
  and explicit installation. It is not used by the workflow scheduler.
- `tests/`, `CMakeWorkflow.Profiling.Tests/` and
  `CMakeWorkflow.Campaigns.Tests/` contain executable checks.

The generic integration suite creates temporary CMake/Ninja projects and
checks graph execution, context lifecycle, cancellation, leases, regeneration,
multi-config resolution and read-only reporting:

```sh
dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -c Release -- bin/Release/net10.0/CMakeWorkflow
```

Unix SIGINT cases run on Unix hosts. Windows cancellation behavior requires
execution on a Windows runner.

## Tool provisioning

Tool setup is an explicit operation. Workflow execution never installs tools,
and the standalone CMake toolkit keeps working without this .NET CLI.

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

A complete local run uses the existing command names:

```sh
CMakeWorkflow tooling doctor --profile native
CMakeWorkflow tooling plan --profile native
CMakeWorkflow context inspect out/build/release
CMakeWorkflow workflow run workflow.json --results-dir out/results
CMakeWorkflow history runs
```

Completed workflow runs are indexed automatically. Use
`tooling setup --profile native --yes` if doctor reports missing tools.

Profiles are `native`, `cmake`, `ninja`, `profiling`, `fuzz`, `development` and
`all`. `native` checks CMake, CTest, a C++ compiler and Ninja. Profiles can be
passed by position or with repeatable `--profile`.
Profiling selects `perf`, WPR or `xctrace` according to the host OS. Doctor and
plan are read-only. Setup re-probes tools, attempts a mapped package through
apt, pacman, dnf, Homebrew or winget, then verifies that the command and its
required version are available. `--no-fallback` disables standalone releases;
`--pm none` disables package installation. Mutating setup requires `--yes`.
`--install-dir` selects a user bin directory; add it to `PATH` for future
sessions. Missing tools without a supported installer receive a manual
diagnostic. Tool availability does not imply profiling permissions or a
compatible kernel tracing configuration. On Linux, package setup uses
non-interactive `sudo`; arrange authorization first or run setup as root.

On Linux x64/arm64, missing CMake or CTest can fall back to an official Kitware
release only when its GitHub asset publishes a SHA-256 digest. The archive is
verified before extraction. Other unverified installer scripts are not run.
Set `CMAKEWORKFLOW_TOOL_STORE` to choose the local release store. Setup supports
no automatic Windows ADK or macOS Xcode installation; those OS backends are
diagnosed with manual guidance.


### Explicit DagFlow harness ingestion (opt-in)

An `invoke` node may declare `harnessDirectory`, **relative to that context's build tree**:

```json
{
  "id": "api-workloads",
  "kind": "invoke",
  "context": "runtime",
  "target": "run_dagflow_api_campaign",
  "harnessDirectory": "harness-results/Release/my-unique-api-campaign"
}
```

The native target must be configured to emit `manifest.json`, `summary.json` and
`runs.jsonl` to that exact directory. The directory must be empty or absent
**before** the invoke starts. Configure a unique harness results directory for
each experiment; fixed/reused native output directories cannot establish
run-to-output ownership and are rejected instead of silently attributing stale
measurements to the new run. CMakeWorkflow does not guess or scan output paths.

After a successful invoke, the CLI snapshots the three files under the workflow
run's `harness/<node-id>/` directory. `run.json` and the JSONL event stream
reference these immutable-per-run copies. The History importer verifies the
manifest SHA-256 when present, raw record identity and sample counts, and indexes
successful non-warmup metric samples by node, case and metric. Existing workflows
without this optional field are unaffected.

Raw samples are indexed in their original executable metric units (summary
fixed-point `scale` applies to summary statistics, not to raw `output` numbers).
These measurements have method `dagflow-harness`; interpret comparisons as
statistical verdicts only when the CLI's comparability/detection diagnostics
permit it. Instrumented diagnostic builds must not be compared as timing baselines.

## Machine-readable CLI

Use `--json` for structured discovery, plans, saved reports and history:

```sh
CMakeWorkflow context inspect out/hello --json
CMakeWorkflow target resolve --build-dir out/hello --target hello --json
CMakeWorkflow workflow plan examples/hello/workflow.json --json
CMakeWorkflow workflow report RUN_DIRECTORY --json
CMakeWorkflow history import out/results --recursive --db out/history.sqlite --json
CMakeWorkflow history compare BASELINE_RUN CANDIDATE_RUN --db out/history.sqlite --json
CMakeWorkflow history detect BASELINE_RUN CANDIDATE_RUN --metric process_wall_time --direction higher-worse --min-percent 5 --db out/history.sqlite --json
```

`workflow run FILE --json` writes one compact JSON object per line to stdout.
Each event has `schemaVersion: 1`, a run-local `sequence`, timestamp, type,
run directory, optional node identity/status/exit code/error and available
artifact paths. Events include `workflow_started`, `node_started`,
`node_finished` and `workflow_finished`. A node's terminal status distinguishes
success, failure, cancellation and blocked dependencies. Diagnostics remain on
stderr. Without `--json`, the workflow still writes the event journal but does
not mirror it to stdout.

The same lines are flushed to `events.jsonl` in the run directory before they
are printed. A reconnecting client can read this journal and then `run.json`.
`run.json` is the authoritative workflow state; events are live notifications
and may be missing from a truncated stream after a process or connection
failure. Closing stdout does not cancel the workflow; the journal continues to
record events. The journal is created per run and never used to resume execution.

## Local history

The CLI indexes a completed workflow after execution, including failed and
canceled runs. Profile runs are indexed after analysis so their derived reports
are available. The scheduler itself does not depend on SQLite. The original
result directories remain authoritative; the SQLite file is a disposable
index and can be rebuilt by importing result roots again.

Use `workflow run FILE --history-db PATH` to select a catalog, or set
`CMAKEWORKFLOW_HISTORY_DB` for both runs and history queries. `--no-history`
disables automatic indexing for a run. If indexing fails, `run.json` remains
intact, the workflow exit status is preserved, and the CLI reports the database
error on stderr. `history import`
remains available for old, moved or external result directories.

```sh
CMakeWorkflow history import out/results --recursive --db out/history.sqlite
CMakeWorkflow history runs --db out/history.sqlite --limit 50
CMakeWorkflow history runs --db out/history.sqlite --status failed --json
CMakeWorkflow history show RUN_ID --db out/history.sqlite
CMakeWorkflow history metrics --run RUN_ID --db out/history.sqlite --json
CMakeWorkflow history query --metric process_wall_time --variant main --db out/history.sqlite
CMakeWorkflow history compare BASELINE_RUN CANDIDATE_RUN --db out/history.sqlite
CMakeWorkflow history detect BASELINE_RUN CANDIDATE_RUN --metric process_wall_time --direction higher-worse --min-percent 5 --db out/history.sqlite
```

Without `--db`, the catalog lives under the host's local application data
directory at `CMakeWorkflow/history.sqlite`. Queries use the index without
rescanning report files. `history show` checks whether linked artifact paths
still exist, and `history runs` marks missing source directories. A moved
directory can be imported at its new path; it receives a new local run ID.

Completed workflow reports record CPU model, architecture, logical CPU count,
available CPU count, physical topology where the OS exposes it, OS and kernel,
the runner's allowed CPU affinity, and the CPU governor where available. They also record
the CMake generator, configuration, C++ compiler ID/version and cache flags for
each build tree. Node reports retain workload arguments and a hash of explicit
environment overrides; profiling methods identify the backend. The random
local host ID is stored at `CMakeWorkflow/host-id` under local application data.
Set `CMAKEWORKFLOW_STATE_DIR` to use a different local directory. No hostname
or OS machine ID is read to create it. Reports store the host ID, but do not
publish anything automatically.

The importer reads completed `run.json` schema 1, completed fuzz `report.json`
schema 1, standalone `*.results.json` profile captures and campaign reports
with a `campaign-completion.json` marker. Mutation and weak-memory commands
write that marker after finishing, with the report's SHA-256. It also indexes
available `counter-comparison.json` schema 1, single-run perf counters in
`analysis.json`, and ETW normalized summaries. `comparison.json`,
`runs.jsonl` and capture manifests are linked as
artifacts. Raw traces, executables, logs and corpora are never stored in SQLite
or read for indexing. Active reports are skipped. Older mutation and weak-memory
reports without a completion marker cannot be distinguished from partial
reports, so they are skipped.

Database schema version 4 uses `PRAGMA user_version`; opening a fresh database
applies migrations 0→1→2→3→4 transactionally, and an existing version 1–3 catalog is
upgraded in place. Each run records its indexer version so a newer importer
can refresh derived measurements even if source files are unchanged. Imports
are transactional and idempotent. Unsupported report schema versions fail
with the path and expected version.

Comparison retains individual timing samples and reports count, median,
minimum, maximum, mean and population standard deviation. It checks metric,
unit, method, variant, scenario and recorded workload arguments, including
profile capture arguments. Absolute and percentage median changes are emitted
only when recorded characteristics match. Different workloads, local hosts,
toolchains or recorded environments are incompatible; missing information or
an environment that changes during a run is unverified and yields no delta.
Matching fingerprints confirm the recorded characteristics only. CPU frequency,
temperature and background load are not fully attested. Two timing samples per
variant support descriptive comparison, not a reliable regression verdict.
Perf counters and ETW summaries keep distinct methods. `history compare`
remains descriptive. The separate `history detect` command performs a one-sided
permutation test on the difference in sample medians. It requires an explicit
metric, direction (`higher-worse` or `lower-worse`) and practical effect threshold
(`--min-percent`). Both runs must have succeeded, their source directories must
exist, their recorded measurement conditions must be compatible, and each
variant/scenario needs at least 10 distinct individual samples per run. Missing
or incompatible data produce diagnostics without a p-value or regression verdict.

`history detect` uses exact permutations for small sample sets and 9,999
deterministic Monte Carlo permutations for larger sets. It applies Holm's
correction across all tested variant/scenario rows selected by the command;
`--variant` and `--scenario` can narrow that family. `--alpha` defaults to 0.05.
A `regression` result requires both adjusted p-value ≤ alpha and a median change
in the declared worse direction of at least `--min-percent`. `inconclusive` does
not establish equivalence or absence of a regression. The test assumes
exchangeable, sufficiently independent samples under its null hypothesis;
sequential runs may still have thermal drift, frequency changes or background
load that the recorded fingerprint does not capture. Use controlled repetitions
and inspect the raw samples before acting on the verdict.
