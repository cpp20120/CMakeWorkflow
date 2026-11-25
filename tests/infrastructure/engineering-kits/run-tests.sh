#!/usr/bin/env bash
set -euo pipefail

cli=$1
consumer=$2
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
project="$tmp/KitSample"

"$cli" new runtime KitSample "$project"
"$cli" kit list --project "$project" --json | jq -e '.kits | length >= 4'
"$cli" kit show linux-gcc --project "$project" | jq -e '.kit.provider == "AmbientGcc"'
"$cli" project config plan "$project" > "$tmp/v1.json"
"$cli" test --project "$project" --plan --json > "$tmp/legacy.json"
sdk_sha=$(sha256sum "$project/cmwf.sdk.lock.json")
source_sha=$(sha256sum "$project/CMakeLists.txt")

for kit in linux-gcc linux-system-clang "${@:3}"; do
    "$cli" kit use "$kit" --project "$project"
    "$cli" restore --project "$project" --plan --json > "$tmp/restore.json"
    test ! -d "$project/cmwf-kits" || test "$kit" != linux-gcc
    kit_sha=$(jq -er '.kitLockSha256' "$tmp/restore.json")
    if "$cli" kit lock "$kit" --project "$project" --plan-sha "$(printf '%064d' 0)"; then exit 1; fi
    "$cli" kit lock "$kit" --project "$project" --plan-sha "$kit_sha"
    "$cli" kit verify "$kit" --project "$project" >/dev/null
    "$cli" restore --project "$project" --plan --json > "$tmp/restore.json"
    config_sha=$(jq -er '.configurationLockSha256' "$tmp/restore.json")
    "$cli" restore --project "$project" --configuration-plan-sha "$config_sha"
    "$cli" test --project "$project" --plan --json > "$tmp/$kit.plan.json"
    test "$("$consumer" --kit-plan "$project" "$kit")" = "$(jq -er .manifestSha256 "$tmp/$kit.plan.json")"
    "$cli" test --project "$project" --kit "$kit" --no-history --json > "$tmp/$kit.test.json"
    "$cli" benchmark --project "$project" --kit "$kit" --repeats 2 --json > "$tmp/$kit.bench.json"
    jq -e '.status == "succeeded" and .history.state == "indexed"' "$tmp/$kit.bench.json"
    run=$(jq -er .runDirectory "$tmp/$kit.bench.json")
    jq -e --arg kit "$kit_sha" 'any(.nodes[]; .kitLockSha256 == $kit) and any(.nodes[]; .metrics != null)' "$run/run.json"
    "$consumer" --kit-exercise "$kit"
    if [[ "$kit" == linux-clang || "$kit" == linux-clang-multi ]]; then
        "$cli" pack --project "$project" --kit "$kit" --no-history
        build=$(jq -er '.manifest.contexts.release.buildDirectory' "$tmp/$kit.plan.json")
        test -s "$project/$build/cmwf-package.zip"
    fi
done

"$cli" benchmark --project "$project" --kit-matrix linux-gcc,linux-system-clang --repeats 2 --jobs 2 --plan --json > "$tmp/matrix-plan.json"
jq -e '(.kitMatrix | length == 2) and ([.manifest.nodes[] | select(.kind == "run") | .benchmarkTrial.axes[] | select(.name == "kit") | .value] | unique | length == 2)' "$tmp/matrix-plan.json"
"$cli" benchmark --project "$project" --kit-matrix linux-gcc,linux-system-clang --repeats 2 --jobs 2 --json > "$tmp/matrix.json"
matrix_run=$(jq -er 'select(.status == "succeeded" and .history.state == "indexed").runDirectory' "$tmp/matrix.json")
jq -e '([.toolchains[].environmentProfileId] | unique | length == 2) and ([.nodes[] | select(.kind == "run" and .status == "succeeded")] | length == 4)' "$matrix_run/run.json"
if "$cli" benchmark --project "$project" --kit-matrix linux-gcc,windows-msvc --plan; then exit 1; fi

first=$(jq -er '.manifest.contexts.release.buildDirectory' "$tmp/linux-gcc.plan.json")
second=$(jq -er '.manifest.contexts.release.buildDirectory' "$tmp/linux-system-clang.plan.json")
test "$first" != "$second"
"$cli" pack --project "$project" --kit linux-gcc --no-history
test -s "$project/$first/cmwf-package.zip"
"$cli" kit use linux-gcc --project "$project"
"$cli" test --project "$project" --plan --json > "$tmp/return.json"
cmp "$tmp/linux-gcc.plan.json" "$tmp/return.json"
"$cli" kit clear --project "$project"
"$cli" project config plan "$project" > "$tmp/v1-after.json"
cmp "$tmp/v1.json" "$tmp/v1-after.json"
"$cli" test --project "$project" --plan --json > "$tmp/legacy-after.json"
cmp "$tmp/legacy.json" "$tmp/legacy-after.json"
test "$sdk_sha" = "$(sha256sum "$project/cmwf.sdk.lock.json")"
test "$source_sha" = "$(sha256sum "$project/CMakeLists.txt")"

manifest=$("$consumer" --kit-stage "$project" linux-gcc)
jq '.contexts.release.toolchain.environmentProfileId = "linux-ambient-clang"' "$manifest" > "$project/mismatched.json"
if "$cli" workflow plan "$project/mismatched.json" > "$tmp/mismatched.log" 2>&1; then exit 1; fi
grep -F variant-context-mismatch "$tmp/mismatched.log"

"$cli" kit show linux-gcc --project "$project" | jq '{schemaVersion:1,kits:[.kit|.id="custom-gcc"]}' > "$tmp/custom-kit.json"
cp "$tmp/custom-kit.json" "$project/cmwf.kits.json"
original=$("$cli" kit plan custom-gcc --project "$project" | jq -er .kitLockSha256)
jq '.kits[0].displayName = "Renamed custom GCC"' "$project/cmwf.kits.json" > "$tmp/catalog.json"
cp "$tmp/catalog.json" "$project/cmwf.kits.json"
test "$original" = "$("$cli" kit plan custom-gcc --project "$project" | jq -er .kitLockSha256)"
jq '.kits[0].id = "linux-gcc"' "$tmp/catalog.json" > "$project/cmwf.kits.json"
if "$cli" kit list --project "$project"; then exit 1; fi

printf '{"schemaVersion":1,"schemaVersion":1,"kits":[]}' > "$project/cmwf.kits.json"
if "$cli" kit list --project "$project"; then exit 1; fi
echo 'Engineering Kits acceptance passed.'
