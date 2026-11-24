#!/usr/bin/env bash
set -euo pipefail
cli=$1
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
store=${CMAKEWORKFLOW_TOOL_STORE:-}
export CMAKEWORKFLOW_TOOL_STORE="$root/store"
reject() {
    if "$@" >"$root/reject.out" 2>"$root/reject.err"; then
        echo "Unexpected success: $*" >&2
        exit 1
    fi
}
"$cli" tooling catalog >"$root/catalog.json"
grep -q '"treeSha256"' "$root/catalog.json"
reject "$cli" tooling resolve --profile linux-clang
grep -q '"missing"' "$root/reject.out"
test ! -e "$CMAKEWORKFLOW_TOOL_STORE"
reject "$cli" tooling resolve --profile linux-clang --target-arch arm64
grep -q '"unsupported-host-target"' "$root/reject.out"
reject "$cli" tooling resolve --profile linux-clang --version ninja=missing
grep -q '"component-unavailable"' "$root/reject.out"
if [[ -z "$store" ]]; then
    echo 'Toolchain P2 read-only checks passed.'
    exit 0
fi

export CMAKEWORKFLOW_TOOL_STORE="$store"
"$cli" tooling resolve --profile linux-clang >"$root/resolution.json"
sha=$(jq -er '.environmentSha256' "$root/resolution.json")
jq -e '.ready and .environment.schemaVersion == 2 and .environment.target.triple == "x86_64-unknown-linux-gnu"
    and .binding.environmentLockSha256 == .environmentSha256' "$root/resolution.json"
"$cli" tooling lock --profile linux-clang --approve-sha "$sha"
"$cli" tooling verify --environment-lock "$sha"
cpack=$("$cli" tooling doctor --profile packaging --tool-policy ManagedOnly --json |
    jq -er '.tools[] | select(.id == "cpack" and .origin == "managed") | .command')
for type in library application; do
    "$cli" project new "$type" Sample "$root/$type" --environment-lock "$sha"
    "$cli" project sdk workflow "$root/$type" >"$root/$type/resolved.json"
    "$cli" workflow run "$root/$type/resolved.json" --results-dir "$root/results-$type" --no-history
    run=$(find "$root/results-$type" -name run.json -print -quit)
    jq -e --arg sha "$sha" 'any(.toolchains[]; .linux.environmentLockSha256 == $sha)' "$run"
    "$cpack" --config "$root/$type/out/build/managed-linux/CPackConfig.cmake" -C Release -G TGZ -B "$root/$type/packages"
    compgen -G "$root/$type/packages/*.tar.gz" >/dev/null
    jq '.contexts.project.toolchain.generator = "Ninja" | .contexts.project.generator = "Ninja"' \
        "$root/$type/resolved.json" > "$root/$type/wrong-generator.json"
    reject "$cli" workflow run "$root/$type/wrong-generator.json" --results-dir "$root/wrong-$type" --no-history
    grep -q 'generator differs' "$root/reject.err"
done
if [[ $# == 2 ]]; then
    recovery_cli=$2
    marker="$root/fault-ready"
    CMAKEWORKFLOW_TEST_FAULT=before-dispatch CMAKEWORKFLOW_TEST_NODE=build CMAKEWORKFLOW_TEST_MARKER="$marker" \
        "$recovery_cli" workflow run "$root/library/resolved.json" --results-dir "$root/recovery" --no-history \
        >"$root/recovery.log" 2>&1 &
    process=$!
    for ((attempt=0; attempt<150; attempt++)); do
        [[ -s "$marker" ]] && break
        kill -0 "$process" 2>/dev/null || { cat "$root/recovery.log" >&2; exit 1; }
        sleep 0.2
    done
    kill -KILL "$process"
    wait "$process" 2>/dev/null || true
    test -s "$marker"
    checkpoint=$(find "$root/recovery" -name run.json -print -quit)
    run_dir=$(dirname "$checkpoint")
    status=0
    "$recovery_cli" workflow resume "$run_dir" || status=$?
    test "$status" = 4
    expected=$(sha256sum "$checkpoint" | awk '{print $1}')
    node=()
    if jq -e '.nodes[] | select(.id == "build" and .status == "outcome_unknown")' "$checkpoint" >/dev/null; then
        node=(--node build)
    fi
    "$recovery_cli" workflow reconcile "$run_dir" --decision retry --reason 'Fixture stopped before build dispatch' \
        --expected-checkpoint "$expected" --confirm-stopped "${node[@]}"
    CMAKEWORKFLOW_TOOL_STORE="$root/missing-store" reject "$recovery_cli" workflow resume "$run_dir"
    "$recovery_cli" workflow resume "$run_dir"
    jq -e '.status == "succeeded"' "$checkpoint"
    echo 'Toolchain P2 resume rejected a missing environment and continued with the approved environment.'
fi
echo 'Toolchain P2 locked Library and Application checks passed.'
