# CLI reference

**[English](cli.md) · [Русский](../cli.md) · [Index](README.md)**

The CLI command contract is implemented in [Program.cs](../../Program.cs) and its subcommand handlers. Below, `CMakeWorkflow` denotes a published executable or a launcher on PATH; for source development use `dotnet bin/Release/net10.0/CMakeWorkflow.dll` instead. Replace all uppercase placeholders with real values and quote paths with spaces.

## Build and NativeAOT publish

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
```

Choose a runtime identifier appropriate to the host; a native Windows executable has the `.exe` suffix. Building from source requires the SDK; a framework-dependent DLL requires a matching runtime. NativeAOT removes the .NET runtime deployment requirement but does not bundle CMake, Ninja, a C++ compiler or profiling tools.

## Inspect and execute native targets

```sh
CMakeWorkflow context inspect BUILD_DIR --json
CMakeWorkflow target resolve --build-dir BUILD_DIR --target TARGET --config Release --json
CMakeWorkflow target build --build-dir BUILD_DIR --target TARGET --config Release
CMakeWorkflow target invoke --build-dir BUILD_DIR --target CUSTOM_TARGET --config Release
CMakeWorkflow target run --build-dir BUILD_DIR --target EXECUTABLE --config Release -- ARG1 "argument with spaces"
```

`inspect` and `resolve` read metadata. `build` is for native executable/library targets; `invoke` explicitly permits custom/utility targets. `run` requests a native build before executing an executable. Arguments after `--` belong to the child executable, not CMakeWorkflow. The special `--prepared` path is for the CMake target bridge with declared native prerequisites, not a blanket “skip builds” switch.

## Workflow

```sh
CMakeWorkflow workflow plan FILE --only NODE_A --only NODE_B --json
CMakeWorkflow workflow run FILE --only NODE_A --jobs 2 --results-dir DIR --history-db CATALOG --json
CMakeWorkflow workflow run FILE --no-history --json
CMakeWorkflow workflow report RUN_DIRECTORY --json
CMakeWorkflow workflow sequence --build-dir BUILD_DIR --first FIRST --second SECOND --config Release --first-arg ARG --second-arg ARG
```

The default `--jobs` is 1. `--only` is repeatable and includes prerequisites. Without `--results-dir`, the runner creates a unique directory below `out/workflow-runs` beside the manifest; an explicitly relative results directory resolves from CLI cwd. Read the **actual** `runDirectory` from the event stream. `sequence` builds and executes the first target, then builds and executes the second; it is distinct from the journaled general workflow. `--controller-build-dir` is an internal safety constraint against recursive builds and should not be fabricated by an IDE.

## History

```sh
CMakeWorkflow history import RESULTS_ROOT --recursive --db CATALOG --json
CMakeWorkflow history runs --limit 50 --status failed --db CATALOG --json
CMakeWorkflow history show RUN_ID --db CATALOG --json
CMakeWorkflow history metrics --run RUN_ID --metric METRIC --variant VARIANT --scenario CASE --db CATALOG --json
CMakeWorkflow history query --metric METRIC --run RUN_ID --db CATALOG --json
CMakeWorkflow history compare BASELINE_ID CANDIDATE_ID --db CATALOG --json
CMakeWorkflow history detect BASELINE_ID CANDIDATE_ID --metric process_wall_time --direction higher-worse --min-percent 5 --alpha 0.05 --db CATALOG --json
```

`runs` also supports `--since`, `--until` (ISO timestamps), `--workflow` and `--variant`. `metrics` requires `--run`, while `query` requires `--metric`; both support filtering. `detect` can narrow the family via `--variant` and `--scenario`. Use `--db` for query commands, `--history-db` or `--no-history` for workflow/profile execution (not both). `CMAKEWORKFLOW_HISTORY_DB` selects a shared default.

## Profiling and DagFlow campaigns

```sh
CMakeWorkflow profile run --build-dir BUILD_DIR --target EXECUTABLE --backend auto --mode cpu --repeats 3 --config Release --results-dir out/profiles -- PROGRAM_ARG
CMakeWorkflow profile run --variant before=BUILD_A --variant after=BUILD_B --target EXECUTABLE --mode counters --repeats 3
CMakeWorkflow profile analyze RUN_DIRECTORY
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY --node NODE --backend auto --results-dir out/profiles
CMakeWorkflow dagflow mutation run --source-dir SOURCE_DIR --suite all
CMakeWorkflow dagflow weak-memory run --source-dir SOURCE_DIR --protocol all --relacy RELACY_ROOT
CMakeWorkflow dagflow fuzz seed --layer wake_protocol --output CORPUS_ROOT
CMakeWorkflow dagflow fuzz run --build-dir BUILD_DIR --layer wake_protocol --duration 60 --results-dir out/fuzz-runs
CMakeWorkflow dagflow fuzz triage --run-dir RUN_DIRECTORY
CMakeWorkflow dagflow fuzz merge --run-dir RUN_DIRECTORY --output CORPUS_DIR
```

`profile replay` operates on **saved directories**, unlike History compare/detect, which take run IDs. Direct executable replay does not take `--case`; an invoke/harness replay may. See [profiling and campaigns](profiling-and-campaigns.md).

## Tooling

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --profile profiling --pm auto --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

Package managers: `auto`, `none`, `apt`, `pacman`, `dnf`, `brew`, `winget`. Options include `--no-fallback`, `--install-dir`. Install actions are explicit, never implicit within workflow execution. See [integrations](integrations.md#tool-provisioning).

## Exit status and machine-readable output

A `workflow run` returns **0** for success, **1** for failure and **130** for user cancellation. Some command-dispatch and tooling cases use **2**; it is not a universal meaning across commands. Recovery has an additional **4** for an interrupted outcome-unknown checkpoint. `workflow report` exit code 0 only says the report could be read. `history detect` can exit successfully with a **regression** verdict; inspect its JSON.

Keep stdout, stderr, spawn errors, exit status and termination signals separate. `--json` is supported by the relevant handlers, not every command. Workflow execution emits JSONL; profile run/analyze/replay and DagFlow campaign commands **do not share one universal JSON completion protocol**. For editor integrations, consume the command-specific result and saved artifacts instead of guessing names.
