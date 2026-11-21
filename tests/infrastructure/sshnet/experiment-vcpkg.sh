#!/usr/bin/env bash
set -Eeuo pipefail
# Requires an isolated SSH fixture with linux-clang, vcpkg, and tooling lock --write.
cli=$1
project=$2
container=$3
mkdir "$project"
cat > "$project/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(vcpkg_experiment LANGUAGES CXX)
find_package(cxxopts CONFIG REQUIRED)
add_executable(benchmarks main.cpp)
target_link_libraries(benchmarks PRIVATE cxxopts::cxxopts)
enable_testing()
add_test(NAME package_consumer COMMAND benchmarks)
CMAKE
cat > "$project/main.cpp" <<'CPP'
#include <cxxopts.hpp>
#include <iostream>
int main() {
    cxxopts::Options parser("package-consumer", "approved vcpkg experiment");
    std::cout << parser.help();
}
CPP
cat > "$project/vcpkg.json" <<'JSON'
{"builtin-baseline":"84bab45d415d22042bd0b9081aea57f362da3f35","dependencies":["cxxopts"]}
JSON
git -C "$project" init -qb main
git -C "$project" add .
git -C "$project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm pinned
commit=$(git -C "$project" rev-parse HEAD)
args=(--repo "file://$project" --ref main --host "$CMWF_SSHNET_HOST" --workspace "$CMWF_SSHNET_WORKSPACE"
  --endpoint "$CMWF_SSHNET_ENDPOINT" --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY"
  --host-key-sha256 "$CMWF_SSHNET_PIN" --port "$CMWF_SSHNET_PORT" --remote-executable "${CMWF_SSHNET_VCPKG_EXE:-$CMWF_SSHNET_EXE}"
  --managed-clang --config Release --vcpkg --benchmark-target benchmarks --repeats 3)
plan=$("$cli" experiment plan "${args[@]}" | awk '/^Plan ID / {print $3}')
dir=$CMAKEWORKFLOW_EXPERIMENT_STORE/$plan
printf '{}\n' > "$project/vcpkg.json"
git -C "$project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qam moved
if "$cli" experiment plan "${args[@]}" >"$dir/baseline.log" 2>&1; then exit 1; fi
grep -F 'explicit builtin-baseline' "$dir/baseline.log"
"$cli" experiment deps plan "$plan" > "$dir/dependencies.log"
sha=$(awk '/^Dependency plan SHA-256 / {print $4}' "$dir/dependencies.log")
jq -e --arg sha "$sha" --arg commit "$commit" '.dependencyPlanSha256 == $sha and .commit == $commit' "$dir/plan.json"
if "$cli" experiment run --plan "$plan" --approve >"$dir/missing.log" 2>&1; then exit 1; fi
grep -F 'is missing' "$dir/missing.log"
if "$cli" experiment deps apply "$plan" --approve --plan-sha "$(printf '%064d' 0)" >"$dir/wrong.log" 2>&1; then exit 1; fi
stage="$CMWF_SSHNET_WORKSPACE/.cmakeworkflow/experiments/$plan/source/vcpkg.json"
docker exec --user cmwf "$container" sh -c 'cp "$1" "$1.backup"; printf " " >> "$1"' sh "$stage"
if "$cli" experiment deps apply "$plan" --approve --plan-sha "$sha" >"$dir/source-changed.log" 2>&1; then exit 1; fi
grep -F 'Materialized source changed' "$dir/source-changed.log"
docker exec --user cmwf "$container" sh -c 'mv "$1.backup" "$1"' sh "$stage"
installer=(experiment deps apply "$plan" --approve --plan-sha "$sha")
environment=$(jq -r '.contexts.experiment.toolchain.environmentLockSha256' < <(jq -r '.manifest' "$dir/plan.json"))
docker exec --user cmwf "$container" sh -c '
  lock="$CMAKEWORKFLOW_TOOL_STORE/manifests/$1.json"
  cp "$lock" "$lock.backup"; printf " " >> "$lock"
' sh "$environment"
if "$cli" "${installer[@]}" >"$dir/environment.log" 2>&1; then exit 1; fi
grep -F 'environment lock differs' "$dir/environment.log"
docker exec --user cmwf "$container" sh -c '
  lock="$CMAKEWORKFLOW_TOOL_STORE/manifests/$1.json"
  mv "$lock.backup" "$lock"
' sh "$environment"
# Exercise a stopped install in the disposable container. SIGSTOP holds the tool
# before publication, SIGKILL forces a real failure, then the same approval retries.
"$cli" "${installer[@]}" > "$dir/interrupted.log" 2>&1 &
interrupted=$!
stopped=''
for ((attempt=0; attempt<200; attempt++)); do
  stopped=$(docker exec "$container" sh -c 'pgrep -x vcpkg | head -n 1')
  if [[ -n "$stopped" ]] && docker exec "$container" sh -c 'kill -STOP "$1" && kill -KILL "$1"' sh "$stopped"; then
    break
  fi
  sleep 0.05
done
test -n "$stopped"
if wait "$interrupted"; then
  echo 'Interrupted dependency install published unexpectedly' >&2
  exit 1
fi
docker exec "$container" sh -c 'test ! -e "$CMAKEWORKFLOW_TOOL_STORE/vcpkg/projects/$1/receipt.json"' sh "$sha"

"$cli" "${installer[@]}" > "$dir/first.log" &
first=$!
"$cli" "${installer[@]}" > "$dir/second.log" &
second=$!
wait "$first"
wait "$second"
tree=$(awk '/^Installed package tree SHA-256 / {print $5}' "$dir/first.log")
test -n "$tree"
grep -F "Installed package tree SHA-256 $tree" "$dir/second.log"
if [[ -n ${CMWF_SSHNET_VCPKG_LOST_REPLY:-} ]]; then
  marker="$CMWF_SSHNET_VCPKG_LOST_REPLY"
  docker exec --user cmwf "$container" touch "$marker.upload"
  docker exec --user cmwf "$container" sh -c 'mv "$1" "$1.legacy"' sh "$marker"
  if "$cli" experiment run --plan "$plan" --approve --wait-seconds 120 >"$dir/lost.log" 2>&1; then exit 1; fi
  operation=$(docker exec "$container" cat "$marker")
  "$cli" experiment report "$plan" --wait-seconds 120
  jq -e --arg operation "$operation" '.operationId == $operation' "$dir/result.json"
else
  "$cli" experiment run --plan "$plan" --approve --wait-seconds 120
fi

result=$dir/result.json
jq -e --arg commit "$commit" --arg sha "$sha" --arg tree "$tree" '
  .commit == $commit and .state == "succeeded"
  and any(.run.toolchains[]; .linux.vcpkgProjectSha256 == $sha and .linux.vcpkgPackageTreeSha256 == $tree)
  and ([.run.nodes[] | select(.kind == "run" and .status == "succeeded")] | length) == 3
' "$result"
artifact=$(jq -er '.artifacts[] | select(.nodeId == "timing_001" and .kind == "stdout") | .artifactId' "$result")
"$cli" experiment download "$plan" --artifact "$artifact" --output "$dir/benchmark.stdout"
grep -F 'approved vcpkg experiment' "$dir/benchmark.stdout"
# On the disposable Host pretend an installed header was corrupted.
docker exec --user cmwf "$container" sh -c 'printf corrupt >> "$CMAKEWORKFLOW_TOOL_STORE/vcpkg/projects/$1/installed/x64-linux-cmwf/include/cxxopts.hpp"' sh "$sha"
if "$cli" "${installer[@]}" >"$dir/corrupt.log" 2>&1; then exit 1; fi
grep -F 'failed SHA-256 verification' "$dir/corrupt.log"
