#!/usr/bin/env bash
set -euo pipefail
cli=$1
shift
sanitizers=0
if [[ ${1:-} == --sanitizers ]]; then
    sanitizers=1
    shift
fi
recovery_cli=${1:-}
tmp=$(mktemp -d)
tmp=$(cd "$tmp" && pwd -P)
trap 'rm -rf "$tmp"' EXIT
export CMAKEWORKFLOW_HISTORY_DB="$tmp/history.sqlite"

reject() {
    local code=$1
    shift
    if "$@" > "$tmp/refusal.out" 2> "$tmp/refusal.err"; then
        echo "Unexpected success: $*" >&2
        exit 1
    fi
    grep -F "$code" "$tmp/refusal.err" "$tmp/refusal.out"
}
plan() {
    "$cli" project config plan "$@" > "$tmp/plan.json" 2> "$tmp/plan.err"
    sed -n 's/^Configuration plan SHA-256: //p' "$tmp/plan.err"
}
publish() {
    local directory=$1 hash=$2
    shift 2
    "$cli" project config lock "$directory" "$@" --plan-sha "$hash"
    "$cli" project config verify "$directory" "$@"
}
workflow() {
    local directory=$1
    shift
    "$cli" project config workflow "$directory" "$@" > "$directory/variants.json"
}
assert_run() {
    local root=$1 variants=$2
    local result
    result=$(find "$root" -name run.json -print -quit)
    jq -e --argjson count "$variants" '.status == "succeeded"
        and ([.nodes[] | .buildVariantId] | unique | length == $count)
        and all(.nodes[]; .status == "succeeded")
        and all(.nodes[] | select(.kind == "configure"); .configure.buildVariantId == .buildVariantId)' "$result"
}
library="$tmp/library"
app="$tmp/app"
"$cli" project new library MatrixLib "$library" --variants
"$cli" project new application VariantApp "$app" --variants
"$cli" project sdk verify "$library"
"$cli" project config list "$library" --json | jq -e '.debug.configuration == "Debug" and .release.configuration == "Release"'
(cd "$library" && cmake --workflow --preset release)
printf '%s\n' '{"optimization":"size","lto":true,"testing":false}' > "$library/team.json"
printf '%s\n' '{"optimization":"default"}' > "$library/override.json"
cat > "$library/cmwf.config.json" <<'JSON'
{
  "schemaVersion": 1,
  "policyFiles": ["team.json"],
  "settings": {"debugInformation": true, "optimization":"speed"},
  "profiles":{
    "release":{"configuration":"Release","lto":false,"optimization":"none","testing":true},
    "debug":{"configuration":"Debug","testing":true}
  },
  "matrix":{
    "profile":"release",
    "axes":{"linkage":["static","shared"],"configuration":["Debug","Release"]},
    "include":[{"configuration":"RelWithDebInfo","linkage":"shared"}],
    "exclude":[{"configuration":"Debug","linkage":"shared"}]
  }
}
JSON

hash=$(plan "$library" --profile release --overrides override.json)
test "${#hash}" = 64
test ! -e "$library/cmwf-config"
jq -e '.variants[0] | .effective.configuration == "Release" and .effective.testing
    and .effective.lto == "Disabled" and .effective.debugInformation == "Enabled"
    and .effective.optimization == "Default" and .origins.optimization == "override:override.json"
    and .origins.lto == "profile:release" and .origins.debugInformation == "project"' "$tmp/plan.json"
cp -a "$library" "$tmp/relocated"
printf '\n// Source edit keeps the same build variant\n' >> "$tmp/relocated/src/library.cpp"
test "$(plan "$tmp/relocated" --profile release --overrides override.json)" = "$hash"
reject stale-configuration-approval "$cli" project config lock "$library" --plan-sha "$hash"
matrix=$(plan "$library" --matrix)
jq -e '[.variants[].effective | .configuration + "/" + .linkage] ==
    ["Debug/Static", "Release/Static", "Release/Shared", "RelWithDebInfo/Shared"]' "$tmp/plan.json"
mkdir -p "$library/cmwf-config/.staging-interrupted"
printf 'preserve\n' > "$library/cmwf-config/.staging-interrupted/user-file"
publish "$library" "$matrix" --matrix &
first=$!
publish "$library" "$matrix" --matrix &
second=$!
wait "$first"
wait "$second"
grep -Fx preserve "$library/cmwf-config/.staging-interrupted/user-file"
workflow "$library" --matrix
jq -e '(.contexts|length) == 4 and (.nodes|length) == 12
    and ([.contexts[].buildDirectory] | unique | length) == 4
    and all(.contexts[]; .buildVariant.id | length == 64)' "$library/variants.json"
"$cli" project config workflow "$library" --matrix --variant variant-03 |
    jq -e '(.contexts | keys) == ["variant-03"] and (.nodes|length) == 3'
"$cli" workflow run "$library/variants.json" --results-dir "$tmp/matrix-run" --jobs 4 --no-history
assert_run "$tmp/matrix-run" 4
test "$(find "$library/out/build" -name libMatrixLib.a | wc -l | tr -d ' ')" = 3
test "$(find "$library/out/build" \( -name libMatrixLib.so -o -name libMatrixLib.dylib \) | wc -l | tr -d ' ')" = 2

lock="$library/cmwf-config/$matrix/lock.json"
cp "$lock" "$tmp/lock.json"
printf '\n' >> "$lock"
reject configuration-lock-unavailable "$cli" project config verify "$library" --matrix
reject configuration-lock-changed "$cli" workflow plan "$library/variants.json"
cp "$tmp/lock.json" "$lock"
mv "$lock" "$tmp/missing.json"
reject lock.json "$cli" workflow plan "$library/variants.json"
mv "$tmp/missing.json" "$lock"
printf '\n' >> "$library/team.json"
reject configuration-input-changed "$cli" workflow run "$library/variants.json" --results-dir "$tmp/stale" --no-history
printf '%s\n' '{"optimization":"size","lto":true,"testing":false}' > "$library/team.json"
printf '\n' >> "$library/cmake/cmwf-sdk/v1/Policy.cmake"
reject 'Vendored SDK module differs' "$cli" project config plan "$library" --matrix
cp "$tmp/relocated/cmake/cmwf-sdk/v1/Policy.cmake" "$library/cmake/cmwf-sdk/v1/Policy.cmake"

jq '.contexts["variant-01"].generator = "Ninja Multi-Config"' "$library/variants.json" > "$library/conflict.json"
reject variant-context-mismatch "$cli" workflow plan "$library/conflict.json"
reject unsafe-input-path "$cli" project config plan "$library" --overrides ../relocated/override.json
reject unsafe-input-path "$cli" project config plan "$library" --overrides CON.json
ln -s "$library/override.json" "$library/linked.json"
reject unsafe-input-link "$cli" project config plan "$library" --overrides linked.json

cp "$app/cmwf.config.json" "$tmp/app-config.json"
for entry in \
    'invalid-configuration-value@{"configuration":"Retail"}' \
    'Unknown or duplicate@{"testing":false,"testing":true}' \
    'Unknown or duplicate@{"CMAKE_CXX_COMPILER":"clang++"}' \
    'unsupported-linkage@{"linkage":"shared"}' \
    'unsupported-instrumentation@{"instrumentation":["thread"]}' \
    'incompatible-instrumentation@{"instrumentation":["address","thread"]}' \
    'incompatible-instrumentation@{"instrumentation":["address","undefined"],"lto":true}'; do
    printf '{"schemaVersion":1,"profiles":{"release":%s}}\n' "${entry#*@}" > "$app/cmwf.config.json"
    reject "${entry%%@*}" "$cli" project config plan "$app" --profile release
done
printf '%s\n' '{"schemaVersion":1,"profiles":{},"matrix":{"axes":{"testing":[true,true]}}}' > "$app/cmwf.config.json"
reject duplicate-variant "$cli" project config plan "$app" --matrix
printf '%s\n' '{"schemaVersion":1,"profiles":{},"matrix":{"axes":{}}}' > "$app/cmwf.config.json"
reject empty-configuration-matrix "$cli" project config plan "$app" --matrix
jq -n '{schemaVersion:1,profiles:{},matrix:{axes:{testing:[range(33)|true]}}}' > "$app/cmwf.config.json"
reject matrix-expansion-limit "$cli" project config plan "$app" --matrix
cp "$tmp/app-config.json" "$app/cmwf.config.json"

app_sha=$(plan "$app" --profile debug)
publish "$app" "$app_sha" --profile debug
workflow "$app" --profile debug
"$cli" workflow run "$app/variants.json" --results-dir "$tmp/app-run" --no-history
assert_run "$tmp/app-run" 1

if ((sanitizers)); then
    export CC=gcc CXX=g++
    printf '%s\n' '{"schemaVersion":1,"profiles":{"release":{"lto":true},"san":{"instrumentation":["address","undefined"]}}}' > "$app/cmwf.config.json"
    ipo_sha=$(plan "$app" --profile release)
    publish "$app" "$ipo_sha" --profile release
    workflow "$app" --profile release
    "$cli" workflow run "$app/variants.json" --results-dir "$tmp/ipo-run" --no-history
    assert_run "$tmp/ipo-run" 1
    grep -F -- '-flto' "$(find "$app/out/build" -name build.ninja -exec grep -l -- '-flto' {} + | head -n 1)"

    cat > "$app/src/main.cpp" <<'CPP'
#include <climits>
#include <cstdlib>
// UBSan's object-size check must not intercept the ASan fixture.
__attribute__((no_sanitize("undefined"))) int address_fixture() {
    volatile int offset = 8;
    char *bytes = static_cast<char *>(std::malloc(1));
    bytes[0] = 1;
    int result = bytes[offset];
    std::free(bytes);
    return result;
}
int main(int argc, char **argv) {
    if (argc == 1) return 0;
    if (argv[1][0] == 'u') {
        volatile int output = INT_MAX;
        return output + argc;
    }
    return address_fixture();
}
CPP
    san_sha=$(plan "$app" --profile san)
    publish "$app" "$san_sha" --profile san
    workflow "$app" --profile san
    "$cli" workflow run "$app/variants.json" --results-dir "$tmp/san-run" --no-history
    assert_run "$tmp/san-run" 1
    tree=$(jq -r '.contexts.san.buildDirectory' "$app/variants.json")
    reject 'signed integer overflow' "$app/$tree/VariantApp" undefined
    reject 'AddressSanitizer: heap-buffer-overflow' "$app/$tree/VariantApp" address
    test ! -f "$app/$tree/CPackConfig.cmake"
    echo 'GCC IPO, ASan and UBSan runtime checks passed.'
fi

if [[ -n ${P3_ENV_LOCK:-} ]]; then
    managed="$tmp/managed"
    "$cli" tooling verify --environment-lock "$P3_ENV_LOCK"
    "$cli" project new library Pinned "$managed" --environment-lock "$P3_ENV_LOCK" --variants
    printf '%s\n' '{"schemaVersion":1,"profiles":{"release":{"configuration":"MinSizeRel"}}}' > "$managed/cmwf.config.json"
    managed_sha=$(plan "$managed" --profile release)
    publish "$managed" "$managed_sha" --profile release
    workflow "$managed" --profile release
    "$cli" workflow run "$managed/variants.json" --results-dir "$tmp/managed-run" --no-history
    assert_run "$tmp/managed-run" 1
    run=$(find "$tmp/managed-run" -name run.json -print -quit)
    jq -e --arg lock "$P3_ENV_LOCK" 'any(.toolchains[]; .linux.environmentLockSha256 == $lock)
        and all(.nodes[]; .configuration == "MinSizeRel")' "$run"
    jq '.contexts.release.toolchain.targetArchitecture = "arm64"' "$managed/variants.json" > "$managed/wrong-target.json"
    reject variant-context-mismatch "$cli" workflow plan "$managed/wrong-target.json"
fi

if [[ -n "$recovery_cli" ]]; then
    marker="$tmp/fault-ready"
    "$cli" project config workflow "$library" --matrix --variant variant-01 > "$library/recovery.json"
    CMAKEWORKFLOW_TEST_FAULT=before-dispatch CMAKEWORKFLOW_TEST_NODE=variant-01-build CMAKEWORKFLOW_TEST_MARKER="$marker" \
        "$recovery_cli" workflow run "$library/recovery.json" --results-dir "$tmp/recovery" --no-history \
        > "$tmp/recovery.log" 2>&1 &
    process=$!
    for ((attempt=0; attempt<150; attempt++)); do
        [[ -s "$marker" ]] && break
        kill -0 "$process" 2>/dev/null || { cat "$tmp/recovery.log" >&2; exit 1; }
        sleep .2
    done
    kill -KILL "$process"
    wait "$process" 2>/dev/null || true
    test -s "$marker"
    checkpoint=$(find "$tmp/recovery" -name run.json -print -quit)
    run_dir=$(dirname "$checkpoint")
    status=0
    "$recovery_cli" workflow resume "$run_dir" || status=$?
    test "$status" = 4
    expected=$(sha256sum "$checkpoint" | awk '{print $1}')
    node=()
    if jq -e '.nodes[] | select(.id == "variant-01-build" and .status == "outcome_unknown")' "$checkpoint" >/dev/null; then
        node=(--node variant-01-build)
    fi
    "$recovery_cli" workflow reconcile "$run_dir" --decision retry --reason "Fixture stopped before dispatch" \
        --expected-checkpoint "$expected" --confirm-stopped "${node[@]}"
    cp "$library/team.json" "$tmp/team.json"
    printf '\n' >> "$library/team.json"
    reject configuration-input-changed "$recovery_cli" workflow resume "$run_dir"
    cp "$tmp/team.json" "$library/team.json"
    "$recovery_cli" workflow resume "$run_dir"
    assert_run "$tmp/recovery" 1
fi
echo 'Project SDK P3 configuration tests passed.'
