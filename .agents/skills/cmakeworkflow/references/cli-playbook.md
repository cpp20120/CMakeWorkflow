# CMakeWorkflow CLI playbook

Examples below assume `CMakeWorkflow` is an executable on `PATH`. Substitute a verified launcher (`cmakeworkflow`, a native executable path, or `dotnet /path/to/CMakeWorkflow.dll`) **without altering argv**. Uppercase placeholders are not literal paths/target names. Prefer read-only commands until execution has been requested.

## Discover, resolve, and run one native target

```sh
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow context inspect BUILD_DIR --json
CMakeWorkflow target resolve --build-dir BUILD_DIR --target EXECUTABLE --config Release --json
CMakeWorkflow target build --build-dir BUILD_DIR --target EXECUTABLE --config Release
CMakeWorkflow target run --build-dir BUILD_DIR --target EXECUTABLE --config Release -- --case chain
```

- `target run` requests a native build before execution. `target invoke` is for custom/utility targets, not `target build`.
- Configure build trees explicitly via project CMake presets or managed workflow. Never infer a preset from a `CMakeCache.txt` entry. `target resolve` and `context inspect` inspect only.
- `tooling plan` is read-only; `tooling setup --profile native --yes` installs and requires explicit user authorization.

## Plan, run, and report a manifest

```sh
CMakeWorkflow workflow plan path/to/workflow.json --json
CMakeWorkflow workflow run path/to/workflow.json --jobs 2 --results-dir out/results --json
CMakeWorkflow workflow report ACTUAL_RUN_DIRECTORY --json
```

- `workflow run --json` prints **JSON Lines**, one event per line. Save stdout and stderr separately if redirecting. Read `runDirectory` from emitted events, then inspect saved `run.json` and `events.jsonl`. The saved report is authoritative; events may be incomplete.
- Exit codes for `workflow run`: `0` success, `1` failure, `130` cancellation. `workflow resume` can return `4` when interrupted work becomes `outcome_unknown`. Do not apply these meanings indiscriminately to all commands.
- `workflow plan --only NODE` or `workflow run --only NODE` includes the node's prerequisites. Validate the entire manifest graph; don't fabricate node IDs.
- In `schemaVersion: 2`, `mode: "managed"` requires an explicit configure node; `mode: "adopt"` refers to an already configured tree, typically with `allowUnverified` only for genuinely unknown provenance. Source/generator/configuration conflicts still fail.
- Relative manifest paths are resolved relative to its file; relative CLI paths are usually relative to cwd.

## Generate typed recipes (generation is not execution)

```sh
CMakeWorkflow workflow generate benchmark \
  --build-dir BUILD_DIR --target EXECUTABLE --config Release \
  --repeats 10 --timeout-seconds 300 --output workflows/bench.json \
  -- --case chain
CMakeWorkflow workflow generate test-matrix \
  --build-dir BUILD_DIR --target TEST_TARGET --config Release \
  --label unit --label integration --order parallel \
  --timeout-seconds 300 --output workflows/tests.json
CMakeWorkflow workflow generate build-matrix \
  --variant debug=BUILD_DEBUG --variant release=BUILD_RELEASE \
  --target EXECUTABLE --output workflows/builds.json
CMakeWorkflow workflow generate profile \
  --build-dir BUILD_DIR --target EXECUTABLE --config Release \
  --platform linux --backend auto --mode cpu --repeats 3 \
  --timeout-seconds 300 --output workflows/profile.json -- --case chain
CMakeWorkflow workflow generate benchmark-matrix --recipe recipes/matrix.json --output workflows/matrix.json
```

After each generation: `workflow plan OUTPUT --json` → (when requested) `workflow run OUTPUT --results-dir ... --json` → `workflow report ACTUAL_RUN_DIRECTORY --json`. `--output` must not already exist. Set `--platform` for a profile recipe deliberately (`linux`, `windows`, `macos` as applicable); do not copy a Linux example onto another host. Recipe JSON schemas live at `schemas/recipes/` in the repository. Generate recipes from verified targets/configurations instead of guessing compiler flags; variant names alone do not set those flags.

## Direct profiling and saved capture analysis

```sh
CMakeWorkflow profile run --build-dir BUILD_DIR --target EXECUTABLE \
  --backend auto --mode cpu --repeats 3 --config Release \
  --results-dir out/profiles -- --case chain
CMakeWorkflow profile analyze SAVED_PROFILE_RUN_DIRECTORY
```

`profile analyze` operates on saved captures. `profile run`, `profile analyze`, campaigns and `workflow run` do **not** all share one JSON streaming protocol. Available backends depend on OS/permissions: Linux perf, Windows ETW, macOS Instruments. Installed tools do not guarantee permission to sample; preserve `partial`/failed capture status.

## History and performance regression

```sh
CMakeWorkflow history runs --limit 50 --db out/history.sqlite --json
CMakeWorkflow history show RUN_ID --db out/history.sqlite --json
CMakeWorkflow history compare BASELINE_RUN_ID CANDIDATE_RUN_ID --db out/history.sqlite --json
CMakeWorkflow history detect BASELINE_RUN_ID CANDIDATE_RUN_ID \
  --metric process_wall_time --direction higher-worse \
  --min-percent 5 --alpha 0.05 --db out/history.sqlite --json
```

History uses **indexed run IDs**, not run directory paths, for `compare/detect`. Saved directories may be indexed with `history import RESULTS_ROOT --recursive --db ... --json`. `history detect` is a one-sided median permutation test and needs ≥10 distinct samples per compared group; success exit code can still return a regression verdict. `unverified`, `incompatible`, `insufficient`, `inconclusive` do not mean performance equivalence. Do not compare differently instrumented/hosted workloads as if environments were interchangeable.

## Root docs

- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/cli.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/workflows.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/recipes.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/results-and-history.md`
