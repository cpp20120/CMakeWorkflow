---
name: cmakeworkflow
description: Use CMakeWorkflow CLI to inspect CMake build trees and targets, plan and execute explicit C++ workflows, generate benchmark/profile/test/build recipes, examine saved run artifacts and performance history, and operate persistent remote SSH Host workflows safely. Trigger when a user asks to build, test, benchmark, profile, run, investigate or recover a project using CMakeWorkflow. Do not invent a pipeline CLI or automatically retry uncertain side effects.
---

# CMakeWorkflow operator skill

Operate the user's existing CMakeWorkflow installation **through its CLI**. This skill provides agent guidance, not MCP or a new executable. Prefer the installed version's own CLI and docs when they differ from these instructions.

## First: determine context and intent

1. Identify the requested action, repository/workspace, CMake source tree, existing **configured** build tree, target, configuration, and whether the user wants inspection only or execution. Inspect the workspace before guessing paths, targets, presets or the binary location.
2. Resolve a launcher: use `CMakeWorkflow` or `cmakeworkflow` from `PATH`, a known native publication, or (when working in the CMakeWorkflow source checkout) `dotnet bin/Release/net10.0/CMakeWorkflow.dll` after an authorized build. Do not assume an installed CLI, invent a subcommand, install dependencies or build CMakeWorkflow itself without need.
3. Run read-only inspection first when appropriate: `context inspect BUILD_DIR --json`, `target resolve --build-dir BUILD_DIR --target TARGET --json`, `tooling doctor --profile native --json`. An unconfigured build tree is **not** made configured by inspection. A missing target is not silently replaced with an assumed target.
4. Choose the smallest operation satisfying the request: inspection → plan → explicit native target or workflow run → authoritative report. Explain any mutation before doing something the user did not request.
5. Read `references/cli-playbook.md` for actual command examples, `references/recovery-remote.md` for interruption/SSH, and `references/architecture-boundaries.md` for capabilities and unsupported interfaces.

## Safety and effect boundaries

- `workflow plan`, `workflow report`, `context inspect`, `target resolve`, `history` read queries, `tooling doctor` and `tooling plan` are inspection operations. `workflow generate` **writes a new manifest** and refuses an existing output. `workflow run`, `target build/run/invoke`, profiling, campaigns, remote start/cancel, resume, and provisioning may execute processes or change state. `tooling setup --yes` installs software; never run it without explicit approval.
- CMakeWorkflow uses CMake for native builds; it does not infer configuration presets, configure missing trees or clean/rebuild implicitly. `target run` first requests the native build. `target invoke` intentionally runs utility/custom targets. Do not bypass known source/generator/configuration conflicts by casually setting `allowUnverified`.
- `workflow plan` checks the graph but does **not** prove the later run will succeed. For v2 manifests, paths inside the manifest resolve relative to **the manifest file**, while relative CLI arguments generally resolve from process working directory. Confirm both.
- Preserve command arguments after `--` as distinct argv entries, including paths with spaces. Do not concatenate user input into a shell command without proper quoting.
- A failed/cancelled/unknown run is never a success just because a reporting command returned exit code 0. **`run.json` is authoritative**. JSONL stdout/events are notifications, may be truncated, and are not a recovery database. Inspect the actual saved status and node outcomes.
- **Never automatically retry a dispatch or side-effecting stage after lost output, SSH disconnect or `outcome_unknown`.** Recover by read-only lookup first. Do not invoke `workflow reconcile`, claim `--confirm-stopped`, or retry a non-idempotent operation on the user's behalf without an explicit, evidence-backed decision. See `references/recovery-remote.md`.
- Results paths, Host artifact IDs, digest/provenance and run IDs are distinct identities. Do not invent them by scanning the latest directory or guessing from names. For Host downloads use the operation-bound catalog ID, not a remote filesystem path.
- `Pipelines` P1–P5 (GitHub Actions attach/dispatch and federated artifact facade) are **experimental .NET library APIs**, not a `CMakeWorkflow pipeline ...` CLI or exposed Host/MCP endpoint. If a user requests that interface, state the limitation and use existing `workflow`/`remote` commands where they cover the task. See `references/architecture-boundaries.md`.

## Normal execution sequence

1. Observe and resolve the configured context and target (if relevant).
2. For a generated workflow, generate a **fresh, intentional output file**; inspect its contents. For a handwritten manifest, validate the actual file; use `schemaVersion: 2` and the installed docs. If the IDE configured the build tree, use explicit `mode: "adopt"` and `provenancePolicy: "allowUnverified"` **only when justified**; otherwise use explicit managed `configure` dependencies.
3. `workflow plan MANIFEST --json`; inspect any error/diagnostic and selected dependency closure (`--only` includes prerequisites).
4. If execution was requested, use `workflow run MANIFEST --results-dir RESULTS_ROOT --json`, preserving process exit status, stdout JSONL, stderr and the actual `runDirectory` from emitted events. Do not infer a default run directory or claim success from the final event alone.
5. Read `workflow report ACTUAL_RUN_DIRECTORY --json` and the saved `run.json`; check the run and individual node statuses. Report the location of logs/artifacts with validation, and surface incomplete provenance, partial profiling captures, or incomparability.
6. When doing performance analysis, use `history runs/show/compare/detect` and `profile analyze` against the correct identifiers. Do not equate a successful `history detect` process exit with no regression; parse its verdict. Do not compare profiled or cross-host metrics as normal baselines without documented comparability.

## Output contract for agent

Return a concise execution record: **what** was inspected/planned/executed, **exact command(s)**, **exit status**, **authoritative workflow status**, **actual run directory/artifacts**, and any recovery or provenance caveats. When no command ran, say so. On connection loss or `outcome_unknown`, state what is known and what remains uncertain; recommend *read-only* discovery rather than guessing or redispatching.

## Project documentation

Use the exact version installed for authoritative behavior. Source baseline for this skill: `cpp20120/CMakeWorkflow` commit `a8479bd67c399fcc4f5470971e72d61a0f23104e` (2026-11-07). Relevant repository docs: `docs/en/cli.md`, `docs/en/workflows.md`, `docs/en/recipes.md`, `docs/en/recovery.md`, `docs/en/remote-cli.md`, `docs/en/p5c-federation.md`. Local code may have evolved; inspect it if behavior differs.
