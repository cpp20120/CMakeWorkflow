#!/usr/bin/env bash
# Network opt-in. Use a dedicated store and a disposable native build tree.
set -euo pipefail

if [[ "$(uname -s):$(uname -m)" != Linux:x86_64 ]]; then
    echo "The managed tooling smoke test requires Linux x86-64." >&2
    exit 2
fi

cli=$(realpath "${1:?Pass the path to the CMakeWorkflow CLI}")
profile=cmake-ninja
binding=linux-managed-tools-system-compiler
if [[ "${2:-}" == --clang || "${2:-}" == --vcpkg ]]; then
    profile=linux-clang
    binding=linux-managed-clang
fi
profiles=(--profile "$profile")
if [[ "${2:-}" == --vcpkg ]]; then
    profiles+=(--profile vcpkg)
fi
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

if "$cli" tooling doctor --tool-policy ManagedOnly "${profiles[@]}"; then
    echo "An empty managed store reported ready." >&2
    exit 1
fi
"$cli" tooling plan --tool-policy ManagedOnly "${profiles[@]}" --json > "$root/plan.json" 2> "$root/approval"
"$cli" host serve --endpoint "$root/host.sock" > "$root/host.log" 2>&1 &
host_pid=$!
for _ in {1..100}; do
    [[ -S "$root/host.sock" ]] && break
    sleep 0.05
done
"$cli" host fix --endpoint "$root/host.sock" --workspace "$root" \
    --tool-policy ManagedOnly "${profiles[@]}" --dry-run --json > "$root/host-plan.json"
grep -qF '"resolutionPolicy":"ManagedOnly"' "$root/host-plan.json"
grep -qF '"sha256":"773cc679c3a7395413bd096523f8e5d6c39f8718af4e12eb4e4195f72f35e4ab"' "$root/host-plan.json"
test ! -e "$CMAKEWORKFLOW_TOOL_STORE"
approval=$(sed -n 's/^Plan SHA-256: //p' "$root/approval")
test "${#approval}" -eq 64
if "$cli" tooling setup --tool-policy ManagedOnly "${profiles[@]}" --yes --plan-sha invalid; then
    echo "An unapproved managed setup succeeded." >&2
    exit 1
fi
test ! -e "$CMAKEWORKFLOW_TOOL_STORE"
if ! "$cli" host fix --endpoint "$root/host.sock" --workspace "$root" \
    --tool-policy ManagedOnly "${profiles[@]}" --yes --json > "$root/host-apply.json"; then
    cat "$root/host-apply.json" >&2
    exit 1
fi
grep -qF '"status":"succeeded"' "$root/host-apply.json"
"$cli" tooling setup --tool-policy ManagedOnly "${profiles[@]}" --yes --plan-sha "$approval"
"$cli" tooling doctor --tool-policy ManagedOnly "${profiles[@]}"

cat > "$root/source/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(ManagedSmoke LANGUAGES C CXX)
enable_testing()
add_library(support STATIC support.c)
add_executable(hello hello.cpp)
target_link_libraries(hello PRIVATE support)
target_compile_features(hello PRIVATE cxx_std_17)
add_test(NAME hello COMMAND hello)
CMAKE

printf '%s\n' 'int support(void) { return 42; }' > "$root/source/support.c"
cat > "$root/source/hello.cpp" <<'CPP'
#include <iostream>

extern "C" int support(void);
int main() {
    std::cout << "managed smoke\n";
    return support() == 42 ? 0 : 1;
}
CPP

selection="\"environmentProfileId\": \"$binding\""
if [[ "$profile" == linux-clang ]]; then
    "$cli" tooling lock --write > "$root/environment.lock.json" 2> "$root/environment.approval"
    environment_lock=$(sed -n 's/^Environment lock SHA-256: //p' "$root/environment.approval")
    cmp "$root/environment.lock.json" "$CMAKEWORKFLOW_TOOL_STORE/manifests/$environment_lock.json"
    selection+=", \"environmentLockSha256\": \"$environment_lock\""
fi

if [[ "${2:-}" == --vcpkg ]]; then
    export XDG_CACHE_HOME="$root/cache"
    export VCPKG_BINARY_SOURCES="clear;files,$root/binary-cache,readwrite"
    mkdir "$XDG_CACHE_HOME" "$root/binary-cache"
    cat > "$root/source/vcpkg.json" <<'JSON'
{"builtin-baseline":"84bab45d415d22042bd0b9081aea57f362da3f35","dependencies":["expected-lite"]}
JSON
    printf '%s\n' '{"registries":[]}' > "$root/source/vcpkg-configuration.json"
    "$cli" tooling vcpkg plan "$root/source" > "$root/vcpkg-plan.json" 2> "$root/vcpkg-approval"
    dependency_plan=$(sed -n 's/^Vcpkg project plan SHA-256: //p' "$root/vcpkg-approval")
    if "$cli" tooling vcpkg install "$root/source" --yes --plan-sha invalid; then
        echo "Unapproved vcpkg install succeeded." >&2
        exit 1
    fi
    "$cli" tooling vcpkg install "$root/source" --yes --plan-sha "$dependency_plan"
    test -n "$(find "$root/binary-cache" -name '*.zip' -print -quit)"
    selection+=", \"vcpkgProjectSha256\": \"$dependency_plan\""
    cat >> "$root/source/CMakeLists.txt" <<'CMAKE'
find_package(expected-lite CONFIG REQUIRED)
target_link_libraries(hello PRIVATE nonstd::expected-lite)
target_compile_definitions(hello PRIVATE CMWF_EXPECTED_LITE)
CMAKE
    sed -i '1i #ifdef CMWF_EXPECTED_LITE\n#include <nonstd/expected.hpp>\n#endif' "$root/source/hello.cpp"
fi

cat > "$root/workflow.json" <<JSON
{
  "schemaVersion": 2,
  "contexts": {
    "app": {
      "buildDirectory": "build",
      "sourceDirectory": "source",
      "mode": "managed",
      "toolchain": { $selection }
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

if [[ "$profile" == linux-clang ]]; then
    grep -qF '"compilerVersion": "21.0.0"' "$report"
    grep -qF '"sdkArtifactSha256": "f851add76bdae472fb6c94b4f920bc4a132d85ac607951391297f636af7e934e"' "$report"
    grep -qF "$root/store/sdks/sysroot/" "$root/build/CMakeCache.txt"
    grep -qF '/bin/ld.lld' "$root/build/CMakeCache.txt"
    grep -qF "$environment_lock" "$report"
fi
if [[ "${2:-}" == --vcpkg ]]; then
    grep -qF "$dependency_plan" "$report"
    grep -qF '"vcpkgPackageTreeSha256"' "$report"
    grep -qF 'VCPKG_MANIFEST_INSTALL:BOOL=OFF' "$root/build/CMakeCache.txt"
    printf '\n' >> "$root/source/vcpkg.json"
    if "$cli" workflow run "$root/workflow.json" --no-history --results-dir "$root/changed-results" > "$root/changed.log" 2>&1; then
        echo "Workflow accepted a changed manifest." >&2
        exit 1
    fi
    grep -qF 'vcpkg project manifest, registry, or toolchain identity changed' "$root/changed.log"
fi

# The same tree rejects a subsequent configure without its managed binding.
sed '/"toolchain"/d; s/"mode": "managed",/"mode": "managed"/' "$root/workflow.json" > "$root/unbound.json"
if "$cli" workflow run "$root/unbound.json" --no-history --results-dir "$root/unbound-results"; then
    echo "Managed build tree accepted an unbound workflow." >&2
    exit 1
fi
report=$(find "$root/unbound-results" -name run.json -print -quit)
grep -qF 'Configured CMake tree has a different toolchain identity' "$report"
echo "Managed Linux workflow smoke test passed ($profile ${2:-})."
