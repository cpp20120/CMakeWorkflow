#!/usr/bin/env bash
# Network opt-in. Use a dedicated store and a disposable native build tree.
set -euo pipefail

if [[ "$(uname -s):$(uname -m)" != Linux:x86_64 ]]; then
    echo "The managed tooling smoke test requires Linux x86-64." >&2
    exit 2
fi

cli=$(realpath "${1:?Pass the path to the CMakeWorkflow CLI}")
root=$(mktemp -d /tmp/cmakeworkflow-managed-smoke.XXXXXXXX)
host_pid=""
cleanup() {
    if [[ -n "$host_pid" ]]; then
        kill "$host_pid" 2>/dev/null || true
        wait "$host_pid" 2>/dev/null || true
    fi
    rm -rf -- "$root"
}
trap cleanup EXIT
export CMAKEWORKFLOW_TOOL_STORE="$root/store"
export XDG_DATA_HOME="$root/share"
export CMAKEWORKFLOW_SMOKE_SECRET="secret-for-managed-tooling-test"
mkdir "$root/source" "$XDG_DATA_HOME"

if "$cli" tooling doctor --tool-policy ManagedOnly; then
    echo "An empty managed store reported ready." >&2
    exit 1
fi
"$cli" tooling plan --tool-policy ManagedOnly --json > "$root/plan.json" 2> "$root/approval"
"$cli" host serve --endpoint "$root/host.sock" > "$root/host.log" 2>&1 &
host_pid=$!
for _ in {1..100}; do
    [[ -S "$root/host.sock" ]] && break
    sleep 0.05
done
"$cli" host fix --endpoint "$root/host.sock" --workspace "$root" \
    --tool-policy ManagedOnly --dry-run --json > "$root/host-plan.json"
grep -qF '"resolutionPolicy":"ManagedOnly"' "$root/host-plan.json"
grep -qF '"sha256":"773cc679c3a7395413bd096523f8e5d6c39f8718af4e12eb4e4195f72f35e4ab"' "$root/host-plan.json"
test ! -e "$CMAKEWORKFLOW_TOOL_STORE"
approval=$(sed -n 's/^Plan SHA-256: //p' "$root/approval")
test "${#approval}" -eq 64
if "$cli" tooling setup --tool-policy ManagedOnly --yes --plan-sha invalid; then
    echo "An unapproved managed setup succeeded." >&2
    exit 1
fi
test ! -e "$CMAKEWORKFLOW_TOOL_STORE"
"$cli" host fix --endpoint "$root/host.sock" --workspace "$root" \
    --tool-policy ManagedOnly --yes --json > "$root/host-apply.json"
grep -qF '"status":"succeeded"' "$root/host-apply.json"
"$cli" tooling setup --tool-policy ManagedOnly --yes --plan-sha "$approval"
"$cli" tooling doctor --tool-policy ManagedOnly

cat > "$root/source/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(ManagedSmoke LANGUAGES C CXX)
enable_testing()
add_executable(hello hello.cpp)
target_compile_features(hello PRIVATE cxx_std_17)
add_test(NAME hello COMMAND hello)
CMAKE

cat > "$root/source/hello.cpp" <<'CPP'
#include <iostream>

int main() {
    std::cout << "managed smoke\n";
}
CPP

cat > "$root/workflow.json" <<'JSON'
{
  "schemaVersion": 2,
  "contexts": {
    "app": {
      "buildDirectory": "build",
      "sourceDirectory": "source",
      "mode": "managed",
      "toolchain": { "environmentProfileId": "linux-managed-tools-system-compiler" }
    }
  },
  "nodes": [
    { "id": "configure", "kind": "configure", "context": "app" },
    { "id": "build", "kind": "build", "context": "app", "target": "hello", "dependsOn": ["configure"] },
    { "id": "test", "kind": "ctest", "context": "app", "dependsOn": ["build"] }
  ]
}
JSON

"$cli" workflow run "$root/workflow.json" --no-history --results-dir "$root/results"
report=$(find "$root/results" -name run.json -print -quit)
grep -qF "$root/store/toolchains/cmake/" "$report"
grep -qF "$root/store/toolchains/ninja/" "$report"
grep -qF '/bin/ctest' "$report"
grep -qF '"origin": "managed"' "$report"
grep -qF '"targetTriple": "x86_64-' "$report"
grep -qF 'CMAKEWORKFLOW_TOOLCHAIN_FINGERPRINT:INTERNAL=' "$root/build/CMakeCache.txt"
if grep -qF "$CMAKEWORKFLOW_SMOKE_SECRET" "$report"; then
    echo "Inherited secret appears in the workflow report." >&2
    exit 1
fi

# The same tree rejects a subsequent configure without its managed binding.
sed '/"toolchain"/d; s/"mode": "managed",/"mode": "managed"/' "$root/workflow.json" > "$root/unbound.json"
if "$cli" workflow run "$root/unbound.json" --no-history --results-dir "$root/unbound-results"; then
    echo "Managed build tree accepted an unbound workflow." >&2
    exit 1
fi
report=$(find "$root/unbound-results" -name run.json -print -quit)
grep -qF 'Configured CMake tree has a different toolchain identity' "$report"
echo "Managed Linux CMake/Ninja workflow smoke test passed."
