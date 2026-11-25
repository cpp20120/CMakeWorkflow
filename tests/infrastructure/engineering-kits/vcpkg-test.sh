#!/usr/bin/env bash
set -euo pipefail

cli=$1
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
project="$tmp/WithVcpkg"

"$cli" new runtime WithVcpkg "$project"
cat > "$project/vcpkg.json" <<'JSON'
{"builtin-baseline":"84bab45d415d22042bd0b9081aea57f362da3f35","dependencies":["expected-lite"]}
JSON
cat >> "$project/CMakeLists.txt" <<'CMAKE'

find_package(expected-lite CONFIG REQUIRED)
target_link_libraries(WithVcpkg_smoke PRIVATE nonstd::expected-lite)
CMAKE
printf '#include <nonstd/expected.hpp>\n' > "$tmp/include"
cat "$project/tests/smoke.cpp" >> "$tmp/include"
cp "$tmp/include" "$project/tests/smoke.cpp"

kit=$("$cli" kit plan linux-clang --project "$project" | jq -er .kitLockSha256)
"$cli" kit lock linux-clang --project "$project" --plan-sha "$kit"
"$cli" restore --project "$project" --kit linux-clang --plan --json > "$tmp/plan.json"
config=$(jq -er .configurationLockSha256 "$tmp/plan.json")
dependency=$(jq -er .vcpkgPlanSha256 "$tmp/plan.json")
# Publish configuration evidence separately from the dependency installation approval.
"$cli" restore --project "$project" --kit linux-clang --configuration-plan-sha "$config" --json > "$tmp/unprepared.json" || {
    jq -e 'any(.diagnostics[]; .code == "DependencyPlanApprovalRequired")' "$tmp/unprepared.json"
}
if "$cli" test --project "$project" --kit linux-clang --no-history > "$tmp/refused.log" 2>&1; then exit 1; fi
grep -F "vcpkg project installation $dependency is missing" "$tmp/refused.log"
"$cli" tooling vcpkg install "$project" --yes --plan-sha "$dependency"
if ! "$cli" test --project "$project" --kit linux-clang --no-history --json > "$tmp/test.json"; then
    cat "$tmp/test.json"
    run=$(jq -r '.runDirectory // empty' "$tmp/test.json")
    if [[ -n "$run" && -f "$run/release-configure.stderr.log" ]]; then
        cat "$run/release-configure.stderr.log"
    fi
    exit 1
fi
run=$(jq -er 'select(.status == "succeeded").runDirectory' "$tmp/test.json")
jq -e --arg dependency "$dependency" '.toolchains[0].linux | .vcpkgProjectSha256 == $dependency and (.vcpkgPackageTreeSha256 | length == 64)' "$run/run.json"
hook=$(find "$project/out/build" -name .cmwf-native-vcpkg-v1.cmake -print -quit)
cp "$hook" "$tmp/hook"
printf '\n' >> "$hook"
if "$cli" test --project "$project" --kit linux-clang --no-history > "$tmp/tampered.log" 2>&1; then exit 1; fi
grep -F 'native vcpkg project hook differs' "$tmp/tampered.log"
cp "$tmp/hook" "$hook"
printf '\n' >> "$project/vcpkg.json"
if "$cli" test --project "$project" --kit linux-clang --no-history; then exit 1; fi
echo 'Engineering Kit vcpkg receipt acceptance passed.'
