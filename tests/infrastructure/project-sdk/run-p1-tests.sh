#!/usr/bin/env bash
set -euo pipefail
if (($# == 0)); then
    echo 'Usage: run-p1-tests.sh CMakeWorkflow [CLI args]' >&2
    exit 2
fi
cli=("$@")
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
project="$root/app"
reject() {
    if "$@" > "$root/refusal.out" 2> "$root/refusal.err"; then
        echo "Unexpected success: $*" >&2; exit 1
    fi
}
plan() {
    "${cli[@]}" project sdk plan "$1" > "$root/plan.json" 2> "$root/plan.err"
    sed -n 's/^SDK plan SHA-256: //p' "$root/plan.err"
}
"${cli[@]}" project sdk list | grep -q 'Native.Application 1.1.0'
"${cli[@]}" project new application SdkApp "$project"
"${cli[@]}" project sdk verify "$project"
cp "$project/cmwf.sdk.lock.json" "$root/initial.lock"
old=$(plan "$project")
test "${#old}" = 64
cmp "$root/initial.lock" "$project/cmwf.sdk.lock.json"
mkdir "$project/policies"
printf '%s\n' '{"cxxStandard":17,"warningsAsErrors":true}' > "$project/policies/team.json"
printf '%s\n' '{"cxxStandard":23,"cxxExtensions":true}' > "$project/policies/local.json"
cat > "$project/cmwf.project.json" <<'JSON'
{"schemaVersion":1,"sdk":"Native.Application","sdkVersion":"1.1.0","policyFiles":["policies/team.json","policies/local.json"],"settings":{"cxxStandard":20}}
JSON
reject "${cli[@]}" project sdk verify "$project"
reject "${cli[@]}" project sdk lock "$project" --plan-sha "$old"
cmp "$root/initial.lock" "$project/cmwf.sdk.lock.json"
reject cmake -S "$project" -B "$root/stale-build" -G Ninja
new=$(plan "$project")
test "$new" != "$old"
"${cli[@]}" project sdk lock "$project" --plan-sha "$new"
"${cli[@]}" project sdk verify "$project"
# Check actual CMake properties, not merely the contents of the generated lock.
cat >> "$project/CMakeLists.txt" <<'CMAKE'
get_target_property(features SdkApp COMPILE_FEATURES)
get_target_property(extensions SdkApp CXX_EXTENSIONS)
get_target_property(warnings SdkApp COMPILE_WARNING_AS_ERROR)
if(NOT "cxx_std_20" IN_LIST features OR NOT extensions OR NOT warnings)
    message(FATAL_ERROR "SDK effective policy did not reach the native target")
endif()
CMAKE
(cd "$project" && cmake --workflow --preset release)
cp -a "$project" "$root/relocated"
# Planning identity must be independent of checkout root (build dirs are not inputs).
test "$(plan "$root/relocated")" = "$new"
"${cli[@]}" project sdk verify "$root/relocated"
cp "$project/cmwf.sdk.lock.json" "$root/valid.lock"
printf '\n' >> "$project/cmwf.sdk.lock.json"
reject "${cli[@]}" project sdk verify "$project"
cp "$root/valid.lock" "$project/cmwf.sdk.lock.json"
cp "$project/policies/local.json" "$root/local.json"
printf '%s\n' '{"cxxExtensions":false}' > "$project/policies/local.json"
reject "${cli[@]}" project sdk verify "$project"
reject cmake --build "$project/out/build/release"
printf '%s\n' '{"unknown":true}' > "$project/policies/local.json"
reject "${cli[@]}" project sdk plan "$project"
printf '%s\n' '{"cxxStandard":20,"cxxStandard":23}' > "$project/policies/local.json"
reject "${cli[@]}" project sdk plan "$project"
rm "$project/policies/local.json"
ln -s "$root/local.json" "$project/policies/local.json"
reject "${cli[@]}" project sdk plan "$project"
rm "$project/policies/local.json"
cp "$root/local.json" "$project/policies/local.json"
"${cli[@]}" project sdk verify "$project"
cp "$project/cmwf.project.json" "$root/project.json"
sed 's/1\.1\.0/9.9.9/' "$root/project.json" > "$project/cmwf.project.json"
reject "${cli[@]}" project sdk plan "$project"
cp "$root/project.json" "$project/cmwf.project.json"
sed 's@policies/local.json@../local.json@' "$root/project.json" > "$project/cmwf.project.json"
reject "${cli[@]}" project sdk plan "$project"
cp "$root/project.json" "$project/cmwf.project.json"
printf '\n# modified module\n' >> "$project/cmake/cmwf-sdk/v1/Native.cmake"
reject "${cli[@]}" project sdk plan "$project"
reject cmake -S "$project" -B "$root/tampered-build" -G Ninja
"${cli[@]}" project new library SdkLib "$root/lib"
"${cli[@]}" project sdk verify "$root/lib"
(cd "$root/lib" && cmake --workflow --preset release)
echo 'Project SDK P1 integration tests passed.'
