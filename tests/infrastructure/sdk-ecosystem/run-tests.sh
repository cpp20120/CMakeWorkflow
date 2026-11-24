#!/usr/bin/env bash
set -euo pipefail
cli=$1
mode=${2:-core}
case "$mode" in core|qt|graphics|all) ;; *) echo "Unknown P4 acceptance mode: $mode" >&2; exit 2 ;; esac
root=$(mktemp -d)
root=$(cd "$root" && pwd -P)
trap 'rm -rf "$root"' EXIT
export CMAKEWORKFLOW_HISTORY_DB="$root/history.sqlite"

reject() {
    local expected=$1
    shift
    if "$@" > "$root/reject.log" 2>&1; then
        echo "Unexpected success: $*" >&2
        exit 1
    fi
    grep -F "$expected" "$root/reject.log"
}
variants() {
    local project=$1
    shift
    "$cli" project config plan "$project" "$@" > "$project/plan.json" 2> "$root/plan.err"
    local digest
    digest=$(sed -n 's/^Configuration plan SHA-256: //p' "$root/plan.err")
    "$cli" project config lock "$project" "$@" --plan-sha "$digest"
    "$cli" project config workflow "$project" "$@" > "$project/variants.json"
    "$cli" workflow run "$project/variants.json" --jobs 2 --no-history --results-dir "$project/results"
}
result() {
    find "$1" -name run.json -print -quit
}

if [[ $mode == core || $mode == all ]]; then
    "$cli" project sdk list --json | jq -e '
        .schemaVersion == 1 and (.sdks | length == 8)
        and any(.sdks[]; .id == "Native.Runtime" and .extends.id == "Native.Library" and .capabilities.benchmarking)
        and any(.sdks[]; .id == "Native.Embedded" and .status == "unsupported")'
    "$cli" project sdk show Native.Interop --version 1.0.0 --json | jq -e '.languages == ["C","CXX"]'
    reject 'unsupported exact SDK version' "$cli" project new runtime Bad "$root/bad" --sdk-version 9.9.9
    reject 'Unknown or abstract SDK' "$cli" project new Native.Base Bad "$root/bad"
    reject 'unsupported-sdk-provider' "$cli" project new embedded Firmware "$root/embedded" --variants
    test ! -e "$root/embedded"

    runtime="$root/runtime"
    "$cli" project new runtime Batch "$runtime" --variants
    "$cli" project sdk verify "$runtime"
    reject 'Project directory must not exist' "$cli" project new runtime Batch "$runtime"
    "$cli" project sdk requirements "$runtime" | jq -e 'any(.requirements[]; .name == "Threads")'
    cat > "$runtime/cmwf.config.json" <<'JSON'
{"schemaVersion":1,"profiles":{"release":{"configuration":"Release"},"asan":{"instrumentation":["address","undefined"]}},
 "matrix":{"axes":{"configuration":["Debug","Release"],"linkage":["static","shared"]}}}
JSON
    variants "$runtime" --matrix
    jq -e '(.nodes | length == 12) and all(.nodes[]; .status == "succeeded")' "$(result "$runtime/results")"
    cp -a "$runtime" "$root/relocated"
    "$cli" project config verify "$root/relocated" --matrix

    (cd "$runtime" && cmake --workflow --preset release)
    cmake --install "$runtime/out/build/release" --prefix "$root/install"
    mkdir "$root/downstream"
    cat > "$root/downstream/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(downstream LANGUAGES CXX)
find_package(Batch 0.1.0 EXACT REQUIRED CONFIG)
add_executable(consumer main.cpp)
target_link_libraries(consumer PRIVATE Batch::Batch)
CMAKE
    cat > "$root/downstream/main.cpp" <<'CPP'
#include <Batch/queue.hpp>
int main() {
    cmwf_runtime::Queue queue;
    std::uint32_t value{};
    return queue.push(42) && queue.pop(value) && value == 42 ? 0 : 1;
}
CPP
    cmake -S "$root/downstream" -B "$root/downstream/build" -G Ninja -DCMAKE_PREFIX_PATH="$root/install"
    cmake --build "$root/downstream/build"
    "$root/downstream/build/consumer"
    "$cli" workflow generate benchmark --build-dir "$runtime/out/build/release" --target Batch_bench \
        --source-dir "$runtime" --preset release --config Release --repeats 2 --timeout-seconds 15 --output "$runtime/bench.json" -- 10000
    "$cli" workflow run "$runtime/bench.json" --results-dir "$runtime/measurements"
    jq -e '[.nodes[] | select(.kind == "run" and .status == "succeeded")] | length == 2' "$(result "$runtime/measurements")"
    "$cli" workflow run "$runtime/bench.json" --results-dir "$runtime/candidate"
    test -s "$CMAKEWORKFLOW_HISTORY_DB"
    "$cli" history runs --status succeeded --limit 2 --json > "$root/bench-history.json"
    jq -e 'length == 2 and all(.[]; .id != null)' "$root/bench-history.json"
    baseline=$(jq -er '.[1].id' "$root/bench-history.json")
    candidate=$(jq -er '.[0].id' "$root/bench-history.json")
    "$cli" history compare "$baseline" "$candidate" --json | jq -e 'type == "array" and length > 0'

    cp "$runtime/cmake/cmwf-sdk/ecosystem-v1/Runtime.cmake" "$root/Runtime.cmake"
    printf '\n' >> "$runtime/cmake/cmwf-sdk/ecosystem-v1/Runtime.cmake"
    reject 'Vendored SDK module differs' "$cli" project sdk verify "$runtime"
    reject 'configuration-input-changed' "$cli" workflow plan "$runtime/variants.json"
    cp "$root/Runtime.cmake" "$runtime/cmake/cmwf-sdk/ecosystem-v1/Runtime.cmake"

    interop="$root/interop"
    "$cli" project new interop Abi "$interop" --variants
    printf '%s\n' '{"schemaVersion":1,"profiles":{"release":{"linkage":"shared"}},"matrix":{"axes":{"linkage":["static","shared"]}}}' > "$interop/cmwf.config.json"
    variants "$interop" --matrix
    jq -e 'all(.nodes[]; .status == "succeeded")' "$(result "$interop/results")"
    (cd "$interop" && cmake --preset release -DBUILD_SHARED_LIBS=ON -DCMWF_INTEROP_MANAGED_TEST=ON &&
        cmake --build --preset release && ctest --preset release)
    grep -F 'ABI round trip: 42' "$interop/out/build/release/Testing/Temporary/LastTest.log"
    reject 'Supply an existing absolute path' dotnet run --project "$interop/managed/Consumer.csproj" \
        --artifacts-path "$interop/out/missing" -- "$interop/does-not-exist"
    reject 'Cannot load the expected native ABI' dotnet run --project "$interop/managed/Consumer.csproj" \
        --artifacts-path "$interop/out/missing" -- "$interop/CMakeLists.txt"

    if [[ -n ${P4_ENV_LOCK:-} ]]; then
        managed="$root/managed"
        "$cli" project new runtime Managed "$managed" --environment-lock "$P4_ENV_LOCK" --variants
        variants "$managed" --profile release
        jq -e --arg lock "$P4_ENV_LOCK" 'any(.toolchains[]; .linux.environmentLockSha256 == $lock)
            and all(.nodes[]; .status == "succeeded")' "$(result "$managed/results")"
    fi
    echo 'P4 catalog, Runtime and Interop acceptance passed.'
fi

if [[ $mode == qt || $mode == all ]]; then
    project="$root/qt"
    "$cli" project new qt Panel "$project" --variants
    reject 'Native.Qt requires installed Qt' cmake -S "$project" -B "$root/no-qt" -G Ninja -DCMAKE_DISABLE_FIND_PACKAGE_Qt6=ON
    reject 'unsupported-sdk-environment' "$cli" project new qt Panel "$root/no-managed-qt" \
        --environment-lock "$(printf '%064d' 0)"
    variants "$project" --matrix
    jq -e '(.nodes | length == 6) and all(.nodes[]; .status == "succeeded")' "$(result "$project/results")"
    (cd "$project" && cmake --workflow --preset release)
    find "$project/out/build/release" -name ui_window.h | grep -q .
    find "$project/out/build/release" -name 'moc_window.cpp' | grep -q .
    find "$project/out/build/release" -name 'qrc_app.cpp' | grep -q .
    grep -F 'CMakeWorkflow Qt resource' "$project/src/banner.txt"
    echo 'P4 headless Qt acceptance passed.'
fi

if [[ $mode == graphics || $mode == all ]]; then
    project="$root/graphics"
    "$cli" project new graphics Compute "$project" --variants
    reject 'Native.Graphics requires installed Vulkan' cmake -S "$project" -B "$root/no-vulkan" -G Ninja -DCMAKE_DISABLE_FIND_PACKAGE_Vulkan=ON
    reject 'Native.Graphics requires glslc' cmake -S "$project" -B "$root/no-glslc" -G Ninja -DCMWF_GLSLC:FILEPATH="$root/missing"
    variants "$project" --matrix
    jq -e '(.nodes | length == 6) and all(.nodes[]; .status == "succeeded")' "$(result "$project/results")"
    test "$(find "$project/out/build" -name probe.comp.spv | wc -l | tr -d ' ')" = 2
    (cd "$project" && cmake --workflow --preset release)
    shader="$project/out/build/release/shaders/probe.comp.spv"
    cp "$shader" "$root/old.spv"
    (cd "$project" && cmake --build --preset release) | grep -F 'no work to do'
    printf '%s\n' '#define WORKGROUP_SIZE 2' > "$project/shaders/values.glsl"
    (cd "$project" && cmake --build --preset release) | grep -F 'Generating shaders/probe.comp.spv'
    if cmp -s "$shader" "$root/old.spv"; then
        echo 'Shader include change did not alter SPIR-V' >&2
        exit 1
    fi
    (cd "$project" && cmake --build --preset release) | grep -F 'no work to do'
    (cd "$project" && cmake --preset release -DCMWF_VULKAN_RUNTIME_TEST=ON && ctest --preset release)
    cp "$(command -v glslc)" "$root/alternate-glslc"
    reject 'Installed SDK dependency changed: glslc' cmake -S "$project" -B "$project/out/build/release" \
        -DCMWF_GLSLC:FILEPATH="$root/alternate-glslc"
    echo 'P4 Vulkan and incremental SPIR-V acceptance passed.'
fi
