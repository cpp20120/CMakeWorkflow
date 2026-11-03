#!/usr/bin/env bash
# Linux acceptance: real CLI -> CMake/Ninja -> shared process dispatcher ->
# elapsed heartbeat during a genuinely silent child (before completion).
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cli="${1:-$project_root/bin/Release/net10.0/CMakeWorkflow}"
cli="$(realpath "$cli")"
[[ -x "$cli" ]] || { echo "CLI missing or not executable: $cli" >&2; exit 2; }
command -v cmake >/dev/null
command -v ninja >/dev/null
fixture="$(mktemp -d -t cmw-progress-cli-XXXXXX)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/source" "$fixture/build/.cmake/api/v1/query/client-cmake-workflow"
for kind in codemodel-v2 cache-v2 toolchains-v1; do
    touch "$fixture/build/.cmake/api/v1/query/client-cmake-workflow/$kind"
done
cat > "$fixture/source/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(CMakeWorkflowSilentProgress LANGUAGES NONE)
add_custom_target(silent COMMAND "${CMAKE_COMMAND}" -E sleep 14 VERBATIM)
CMAKE
cmake -S "$fixture/source" -B "$fixture/build" -G Ninja > "$fixture/configure.log"
cat > "$fixture/workflow.json" <<JSON
{
  "schemaVersion": 2,
  "contexts": {
    "e2e": {
      "sourceDirectory": "$fixture/source",
      "buildDirectory": "$fixture/build",
      "mode": "adopt",
      "provenancePolicy": "allowUnverified"
    }
  },
  "nodes": [{
    "id": "silent-invoke",
    "kind": "invoke",
    "context": "e2e",
    "target": "silent",
    "timeoutSeconds": 40
  }]
}
JSON
"$cli" workflow run "$fixture/workflow.json" \
    --results-dir "$fixture/results" > "$fixture/stdout.log" 2> "$fixture/stderr.log" &
run_pid=$!
seen=0
# Check heartbeat WHILE the CLI is running, not retrospectively in a log.
for ((i=0; i<200; ++i)); do
    if grep -q '\[CMakeWorkflow\] RUNNING silent-invoke' "$fixture/stderr.log"; then
        if kill -0 "$run_pid" 2>/dev/null; then seen=1; fi
        break
    fi
    if ! kill -0 "$run_pid" 2>/dev/null; then break; fi
    sleep 0.1
done
if ! wait "$run_pid"; then
    echo "FAIL: CLI workflow failed. stderr:" >&2
    cat "$fixture/stderr.log" >&2
    exit 1
fi
if ((seen != 1)); then
    echo "FAIL: no live heartbeat observed before silent CMake target completed:" >&2
    cat "$fixture/stderr.log" >&2
    exit 1
fi
if ! find "$fixture/results" -name run.json -print -quit | grep -q .; then
    echo "FAIL: workflow did not write a run.json report" >&2
    exit 1
fi
echo "PASS CLI E2E: observed live heartbeat while silent CMake/Ninja target was still running"
