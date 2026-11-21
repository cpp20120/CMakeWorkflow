#!/usr/bin/env bash
set -Eeuo pipefail
cli=$1
git_project=$2
container=$3
mkdir "$git_project"
cp examples/experiment/* "$git_project"
truncate -s 9M "$git_project/source-payload.bin"
printf '#!/bin/sh\nexit 0\n' > "$git_project/exec-test.sh"
chmod +x "$git_project/exec-test.sh"
printf '\nadd_test(NAME executable_source COMMAND "${CMAKE_CURRENT_SOURCE_DIR}/exec-test.sh")\n'   >> "$git_project/CMakeLists.txt"
git -C "$git_project" init -q -b main
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid add .
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm approved
approved_commit=$(git -C "$git_project" rev-parse HEAD)
git -C "$git_project" tag approved

plan_args=(--repo "file://$git_project" --host "$CMWF_SSHNET_HOST" --workspace "$CMWF_SSHNET_WORKSPACE"
  --endpoint "$CMWF_SSHNET_ENDPOINT" --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY"
  --host-key-sha256 "$CMWF_SSHNET_PIN" --port "$CMWF_SSHNET_PORT"
  --remote-executable /opt/cmwf-experiment-bridge --preset release --ctest
  --benchmark-target benchmarks --repeats 3 --benchmark-arg "$CMWF_SSHNET_WORKSPACE/experiment-effects")
plan=$("$cli" experiment plan "${plan_args[@]}" --ref main | awk '/^Plan ID / {print $3}')
plan_dir=$CMAKEWORKFLOW_EXPERIMENT_STORE/$plan
for revision in refs/tags/approved "$approved_commit"; do
  other=$("$cli" experiment plan "${plan_args[@]}" --ref "$revision" | awk '/^Plan ID / {print $3}')
  jq -e --arg commit "$approved_commit" '.commit == $commit' "$CMAKEWORKFLOW_EXPERIMENT_STORE/$other/plan.json"
done

sed -i 's/approved revision/moved branch/' "$git_project/benchmark.c"
sed -i '1i #include <unistd.h>' "$git_project/benchmark.c"
sed -i '/volatile unsigned long sum/i\    sleep(1);' "$git_project/benchmark.c"
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qam moved
git -C "$git_project" tag slower
ln -s benchmark.c "$git_project/unsupported-link"
git -C "$git_project" add unsupported-link
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm symlink
if "$cli" experiment plan "${plan_args[@]}" --ref main >"$plan_dir/unsupported.log" 2>&1; then
  echo 'Source symlink unexpectedly accepted' >&2
  exit 1
fi
grep -F 'Unsupported Git mode 120000' "$plan_dir/unsupported.log"
if "$cli" experiment run --plan "$plan" >"$plan_dir/unapproved.log" 2>&1; then
  echo 'Unapproved launch unexpectedly succeeded' >&2
  exit 1
fi
"$cli" experiment report "$plan" | grep -F 'pending approval'
cp "$plan_dir/plan.json" "$plan_dir/backup.json"
printf ' ' >> "$plan_dir/plan.json"
if "$cli" experiment run --plan "$plan" --approve >"$plan_dir/tampered.log" 2>&1; then
  echo 'Tampered plan unexpectedly accepted' >&2
  exit 1
fi
grep -F 'plan does not match' "$plan_dir/tampered.log"
mv "$plan_dir/backup.json" "$plan_dir/plan.json"

cp "$plan_dir/source.zip" "$plan_dir/source.zip.saved"
printf ' ' >> "$plan_dir/source.zip"
if "$cli" experiment run --plan "$plan" --approve >"$plan_dir/bad-source.log" 2>&1; then
  echo 'Tampered source ZIP unexpectedly accepted' >&2
  exit 1
fi
grep -F 'archive does not match' "$plan_dir/bad-source.log"
mv "$plan_dir/source.zip.saved" "$plan_dir/source.zip"

if timeout 45s "$cli" experiment run --plan "$plan" --approve >"$plan_dir/upload-lost.log" 2>&1; then
  echo 'Chunk reply unexpectedly delivered' >&2
  exit 1
fi
bundle_hash=$(jq -er '.bundle.sha256' "$plan_dir/plan.json")
upload="$CMWF_SSHNET_WORKSPACE/.cmakeworkflow/experiment-uploads/$plan/$bundle_hash.zip"
test "$(docker exec "$container" stat -c %s "$upload")" = 524288

if timeout 45s "$cli" experiment run --plan "$plan" --approve >"$plan_dir/lost.log" 2>&1; then
  echo 'Start reply unexpectedly delivered' >&2
  exit 1
fi
operation=$(docker exec "$container" cat "$CMWF_SSHNET_WORKSPACE/lost-experiment-operation") || {
  cat "$plan_dir/lost.log" >&2
  exit 1
}
[[ -n "$operation" ]]
result=$plan_dir/result.json
if ! timeout 90s "$cli" experiment report "$plan" --wait-seconds 60; then
  if [[ -f "$result" ]]; then jq '.run.nodes[] | {id, status, error}' "$result" >&2; fi
  exit 1
fi
# Re-opening the experiment after completion returns the same durable child.
"$cli" experiment run --plan "$plan" --approve --wait-seconds 0
test "$(docker exec "$container" cat "$CMWF_SSHNET_WORKSPACE/experiment-effects")" = $'timing\ntiming\ntiming'
jq -e --arg commit "$approved_commit" --arg operation "$operation" '
  .commit == $commit and .operationId == $operation and .state == "succeeded"
  and .run.sourceSnapshotId == .sourceSnapshotId
  and .run.environment != null and (.run.buildEnvironments | length) > 0
  and ([.run.nodes[] | select(.status == "succeeded") | .kind] | sort)
    == ["build","configure","ctest","invoke","run","run","run"]
  and ([.run.nodes[] | select(.kind == "run") | .processDurationSeconds | select(. > 0)] | length) == 3
' "$result"
artifact=$(jq -er '.artifacts[] | select(.nodeId == "timing_001" and .kind == "stdout") | .artifactId' "$result")
"$cli" experiment download "$plan" --artifact "$artifact" --output "$plan_dir/benchmark.stdout"
grep -F 'approved revision' "$plan_dir/benchmark.stdout"
test -s "$plan_dir/report.html"
printf '%s  %s\n' "$(jq -r '.reportSha256' "$result" | tr '[:upper:]' '[:lower:]')" "$plan_dir/run.json" | sha256sum -c
history=''
for ((i=0; i<15; i++)); do
  history=$("$cli" remote history-status --transport sshnet --host "$CMWF_SSHNET_HOST" \
    --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY" --host-key-sha256 "$CMWF_SSHNET_PIN" \
    --port "$CMWF_SSHNET_PORT" --remote-executable "$CMWF_SSHNET_EXE" \
    --endpoint "$CMWF_SSHNET_ENDPOINT" --workspace "$CMWF_SSHNET_WORKSPACE" --operation "$operation")
  if jq -e '.state == "indexed"' <<< "$history"; then break; fi
  sleep 1
done
jq -e '.state == "indexed"' <<< "$history"
echo "PASS Git experiment: pinned SHA, resumable 9 MiB upload, executable source, lost start reply, verified results"
# Run an independently approved revision with a deliberate one-second slowdown.
pool="$git_project/../host-pool.json"
jq -n --arg destination "$CMWF_SSHNET_HOST" --arg workspace "$CMWF_SSHNET_WORKSPACE" \
  --arg endpoint "$CMWF_SSHNET_ENDPOINT" --arg username "$CMWF_SSHNET_USER" --arg keyFile "$CMWF_SSHNET_KEY" \
  --arg hostKeySha256 "$CMWF_SSHNET_PIN" --argjson port "$CMWF_SSHNET_PORT" --arg executable "$CMWF_SSHNET_EXE" '
  {$destination,$workspace,$endpoint,$username,$keyFile,$hostKeySha256,$port,$executable}
  | {hosts:[{name:"missing-workspace",connection:(. + {workspace:(.workspace + "/unavailable")})},
             {name:"ready-host",connection:.}]}' > "$pool"
pool_args=(--repo "file://$git_project" --ref refs/tags/slower --host-pool "$pool"
  --preset release --benchmark-target benchmarks --repeats 3
  --benchmark-arg "$CMWF_SSHNET_WORKSPACE/experiment-effects")
if "$cli" experiment plan "${pool_args[@]}" --min-cpus 2147483647 > "$plan_dir/pool-insufficient.log" 2>&1; then
  echo 'Host accepted with insufficient CPU capacity' >&2
  exit 1
fi
grep -F 'No Host in the pool meets' "$plan_dir/pool-insufficient.log"
candidate=$("$cli" experiment plan "${pool_args[@]}" --min-cpus 1 2>"$plan_dir/pool-selection.log" | awk '/^Plan ID / {print $3}')
candidate_plan=$CMAKEWORKFLOW_EXPERIMENT_STORE/$candidate/plan.json
grep -F 'Host missing-workspace unavailable' "$plan_dir/pool-selection.log"
jq -e '.placement.name == "ready-host" and .placement.resources.availableProcessors >= 1' "$candidate_plan"
# The approved plan owns the selected connection even if the pool file is moved.
mv "$pool" "$pool.saved"
"$cli" experiment run --plan "$plan" --plan "$candidate" --approve --wait-seconds 60
comparison_status=0
"$cli" experiment compare "$plan" "$candidate" --threshold-percent 30 > "$plan_dir/compare.log" || comparison_status=$?
cat "$plan_dir/compare.log"
test "$comparison_status" = 1
grep -F 'Regression' "$plan_dir/compare.log"

CMWF_SSHNET_PLAN="$candidate_plan" CMWF_SSHNET_PLAN_ID="$candidate" \
  timeout 120s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests experiment-sdk
