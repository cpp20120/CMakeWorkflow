# Profiling, replay and DagFlow campaigns

**[English](profiling-and-campaigns.md) · [Русский](../profiling-and-campaigns.md) · [Index](README.md)**

Profiling is an **explicit workflow around an existing CMake target**. CMakeWorkflow builds targets, gathers separate
uninstrumented timing samples and instrumented captures, and retains artifacts for later analysis. Naming a variant `o3`
or `lto` does **not** enable those compiler flags; CMake and your project control the build.

## Profile variants and captures

```sh
CMakeWorkflow profile run --variant normal=out/normal --variant lto=out/lto \
  --target bench --config Release --repeats 10 --backend auto \
  --mode diagnostic --results-dir out/profiles -- --case chain-4
```

`bench` and its arguments are examples: the executable must exist as a target in both configured trees. Use
`--build-dir` for one tree or repeat `--variant NAME=BUILD_DIR` for comparisons; variant names start with a lowercase
letter and contain lowercase letters, digits and `_`. Each variant must have a distinct name and build directory.
`--preset` can configure a single tree but is ambiguous with multiple variants and is rejected there. Use `--source-dir`
and `--preset` when explicit configure is required.

Main options include `--target`, `--config`, `--backend`, `--mode`, `--repeats` (**1–100**), `--timeout-seconds`
(positive, at most **86,400**), `--results-dir`; arguments after `--` are for the executable. Repetition order
alternates variants, reducing—but not eliminating—order bias. The special `--campaign main-o3-lto` accepts
`--o3-build-dir` and `--lto-build-dir` and defaults to `dagflow_stress_bench`.

## Backends and modes

| OS      | Backend       | Backend-supported capture modes   |
|---------|---------------|-----------------------------------|
| Linux   | `perf`        | `cpu`, `scheduler`, `counters`    |
| Windows | `etw`         | `cpu`, `scheduler`                |
| macOS   | `instruments` | `cpu`, `scheduler`, `allocations` |

`--backend auto` selects a host-specific backend. **High-level `profile run` modes are not identical to backend
capabilities**: its parser supports `cpu`, `scheduler`, `counters`, `diagnostic`; the presence of `allocations` in
Instruments does not imply `profile run --mode allocations` works. `profile` nodes inside manifests pass mode through
`options`, validated by the chosen backend. On Linux, higher-level profiling may schedule an additional perf counter
pass when mode is not `counters`.

An installed `perf`, WPR or `xctrace` is not proof that the OS grants necessary privileges, that events can be
collected, or that a capture can be analyzed. In particular, successful `perf stat` does not establish working
`perf record` or scheduler events. A failed capture stays failed even if some partial output could be imported into
History. Cross-backend measurements must not be treated as equivalent without an explicit comparable method.

## Analyze saved outputs

```sh
CMakeWorkflow profile analyze RUN_DIRECTORY
```

Analyze reads **previously saved captures** rather than rerunning the executable. Depending on the run it may produce
`analysis.json`, timing `comparison.json`, `counter-comparison.json`, normalized CPU stack/scheduler reports, and
references to the raw trace and capture manifests. Some of these artifacts are optional. The command reports an actual
`Profile analysis: PATH_TO_ANALYSIS_JSON` path; integrations must check exit code and returned path rather than scanning
for the newest directory.

## Replay a saved baseline/candidate pair

```sh
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY \
  --node RUN_NODE --backend auto --timeout-seconds 300 --results-dir out/profiles
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY \
  --node api-workloads --case chain-4 --harness 0 --backend auto --results-dir out/profiles
```

For a direct `run` node, replay uses a recorded executable identity. For an `invoke` node, it needs a concrete harness
case with a native replay descriptor and a binary snapshot. Replay checks hashes, argv, workload recipe and affinity; a
baseline and candidate sharing a mutable executable path can be rejected. Missing environment evidence may make replay
impossible. A replay is an **instrumented diagnostic experiment**, not new uninstrumented timing samples. Having two
History entries with the same case name does not prove replayability.

## DagFlow harness

An `invoke` workflow node may set `harnessDirectory`, relative to its **build tree**:

```json
{
  "id": "api-workloads", "kind": "invoke", "context": "runtime",
  "target": "run_dagflow_api_campaign",
  "harnessDirectory": "harness-results/Release/unique-campaign"
}
```

The native target must create `manifest.json`, `summary.json` and `runs.jsonl` in exactly that directory. It must be
**absent or empty before invocation**; fixed reused directories do not prove run-to-output ownership and are rejected.
On success CMakeWorkflow snapshots the three files inside the run's own `harness/<node-id>/` directory and connects them
to the report/events. The extension must use recorded paths, not search arbitrary build directories.

History imports successful, non-warmup raw samples under method `dagflow-harness`, verifying manifest identity and
sample counts. Raw sample units are preserved; a summary fixed-point `scale` does **not** scale the raw `output`
records. Timing data from instrumented diagnostic builds should not be used as normal performance baselines without
comparability evidence.

## Optional DagFlow campaigns

| Command                   | Scope                                                                                                                        |
|---------------------------|------------------------------------------------------------------------------------------------------------------------------|
| `dagflow mutation run`    | Mutation tests: source, suite, seed, cases, steps, timeout, report                                                           |
| `dagflow weak-memory run` | Relacy protocol/model runs: source, protocol, compiler, report; optional `--mutant` keeps baselines and selects one mutation |
| `dagflow fuzz seed`       | Seed generation by fuzz layer                                                                                                |
| `dagflow fuzz run`        | Backend-specific corpus campaign and artifacts                                                                               |
| `dagflow fuzz triage`     | Triage saved fuzz run                                                                                                        |
| `dagflow fuzz merge`      | Merge saved corpus/coverage evidence                                                                                         |

Fuzz backends: `libfuzzer`, `aflpp`, `honggfuzz`; they must match the configured build tree. Fuzz duration range is
**1–86,400 seconds**, input timeout **1–3,600 seconds**, max length **1–1,048,576 bytes**. Coverage merge can actually
execute a fuzzer; it is not merely a file copy. Mutation and weak-memory reports use `campaign-completion.json` with a
report SHA-256 to establish completeness. A partially written report is not a completed campaign; cancellation does not
roll back created outputs or corpora. See [CampaignCommand](../../CMakeWorkflow.Campaigns/CampaignCommand.cs).

## Analysis quality and perf counters

A generated profile workflow can be inspected and replayed through the ordinary engine:

```sh
CMakeWorkflow workflow generate profile --build-dir out/build/release --target bench \
  --config Release --repeats 3 --backend perf --mode cpu \
  --output workflows/profile.json -- --case chain
CMakeWorkflow workflow plan workflows/profile.json
CMakeWorkflow workflow run workflows/profile.json --results-dir out/profile-results
CMakeWorkflow profile analyze RUN_DIRECTORY
```

On Linux, uninstrumented timing is separate from `perf stat` PMU counters and `perf record` sampled stacks. CPU capture
requests `cycles:u` with user call chains; it does not lower `perf_event_paranoid` or request privileged kernel call
chains. Scheduler tracepoints have separate permission requirements. `:u` and `:k` identify user and kernel event
domains. The counter report preserves those names and derives IPC (`instructions / cycles`), cache MPKI
(`1000 * cache-misses / instructions`), and branch MPKI (`1000 * branch-misses / instructions`) only from matching
domains. Multiplexed perf values are reported as scaled estimates with their running percentage; unsupported events are
not zero. These metrics do not have automatic equivalence with ETW or Instruments.

`traces/normalized/*.stacks.json` schema v2 stores root-to-leaf stacks, unweighted sample counts, resolved/unresolved
frame counts, and a `complete`, `partial`, or `no_samples` analysis status. `symbolizedSamples` means at least one frame
was resolved; `fullyResolvedSamples` means every retained frame was resolved. Hot-function `selfSamples` counts resolved
leaf samples, not CPU time or inclusive overhead. Build with symbols for better attribution. `analysis.json`
distinguishes `captureStatus` from `analysisStatus`, includes a compact summary, and records decode failures. If
nonempty perf script output has no recognizable sample headers, analysis fails with a nonzero exit code while preserving
the raw trace. Reanalysis verifies the saved trace hash and atomically replaces analytical outputs; it does not rebuild
or execute the target. A small sample count is descriptive evidence, not a regression verdict.

In stack schema v2, `samples` counts recognized sample headers, while `parsedSamples` counts those with one or more
frames. The analyzer retains up to 96 frames per sample and records any truncation. `droppedDistinctStacks` is a lower
bound if the distinct-stack limit is reached.

The CPU normalizer also accepts `perf script` formats with an inline sampled leaf after the event header. For CPU
captures, `analysis.json` reports `reportedSamples` from `perf report`, `sampleCountMatchesReport`, and `parsedSamples`
in the summary. A mismatch is marked `partial`, not silently `complete`; a nonempty capture with no decoded stacks is a
failed analysis. Errors from `perf report` itself are also recorded as `analysisStatus: failed`, while the original
trace remains available. An interrupted capture with a manifest but no committed `.results.json` is recorded as
`capture_incomplete` using its `runs.jsonl` status, instead of disappearing from analysis. Partial decoding lists
concrete quality warnings. Re-run `profile analyze RUN_DIRECTORY` against an existing trace after updating
CMakeWorkflow; no benchmark rerun is required.

## DagFlow historical O3/LTO capture: progress and recovery

```sh
CMakeWorkflow dagflow profile run-historical --source-dir /path/to/DagFlow \
  --output /path/to/new-results --trials 5 --stat-trials 3 --jobs 4
```

The historical capture is Linux-only and requires an admissible `perf record`
(`cycles:u`, DWARF callgraph). The CLI now displays the current stage and command identity, an operation-count progress
indicator and a periodic elapsed heartbeat on stderr. With the default trial counts the protocol has 397 command steps;
**the percentage is completed command operations, not time remaining**. The final SVG/HTML rendering stage is explicitly
reported after those commands. When stderr is redirected, the progress reporter reduces verbosity but still reports
long-running operations. `workflow run` uses the same process dispatcher and emits an elapsed heartbeat for long child
processes.

The historical adapter owns the fixed research schedule and its single
`commands.json` journal. External process execution and cleanup are delegated to the same `WorkflowProcessDispatcher` as
ordinary workflow nodes, and journal updates use the same durable checkpoint writer as `run.json`. Progress is an
observer, not another source of truth. This is **not** a claim that the fixed historical schedule has become a general
WorkflowDocument DAG or that its
`commands.json` is interchangeable with a regular `run.json`.

On `--resume`, previously succeeded commands are accepted only after executable and argument identity, stdout/stderr
hashes, and declared `perf.data`/counter output hashes are checked. A prior command with an unknown outcome or timeout
is not automatically repeated. Derived flamegraphs may be regenerated from verified saved `perf script` output without
resampling. Keep the complete output folder and use a new one if recovery refuses; never force success by editing
`commands.json`.

### Live progress E2E (Linux)

The regular `--resume` smoke only validates progress over already-completed commands. To test the real active path, run
both process-level fixtures:

```sh
dotnet build CMakeWorkflow.slnx -c Release
# 14-second silent process, live TTY/redirected progress, durable resume,
# cancellation of a real shell + descendant, unknown-effect replay refusal:
dotnet CMakeWorkflow.Profiling.Tests/bin/Release/net10.0/CMakeWorkflow.Profiling.Tests.dll --progress-e2e
# Real CLI invoking a silent 14-second CMake/Ninja target; checks that a
# heartbeat appears *before* the CLI exits:
bash scripts/test-progress-e2e.sh
```

Neither test requires DagFlow, `perf`, elevated privileges or a production historical run. The tests exercise the actual
process dispatcher, not a mocked heartbeat timer. They do not replace the historical O3/LTO acceptance run or the
separate `--recovery` fault-injection suite.
