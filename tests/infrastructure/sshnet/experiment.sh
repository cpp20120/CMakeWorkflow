#!/usr/bin/env bash
set -Eeuo pipefail
cli=$1
git_project=$2
container=$3
mkdir "$git_project"
cp examples/experiment/* "$git_project"
git -C "$git_project" init -q -b main
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid add .
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm approved
approved_commit=$(git -C "$git_project" rev-parse HEAD)
git -C "$git_project" tag approved

plan_args=(--repo "file://$git_project" --host "$CMWF_SSHNET_HOST" --workspace "$CMWF_SSHNET_WORKSPACE"
  --endpoint "$CMWF_SSHNET_ENDPOINT" --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY"
  --host-key-sha256 "$CMWF_SSHNET_PIN" --port "$CMWF_SSHNET_PORT"
  --remote-executable /opt/cmwf-experiment-bridge --preset release --ctest
  --benchmark-target benchmarks --repeats 2 --benchmark-arg "$CMWF_SSHNET_WORKSPACE/experiment-effects")
plan=$("$cli" experiment plan "${plan_args[@]}" --ref main | awk '/^Plan ID / {print $3}')
plan_dir=$CMAKEWORKFLOW_EXPERIMENT_STORE/$plan
for revision in refs/tags/approved "$approved_commit"; do
  other=$("$cli" experiment plan "${plan_args[@]}" --ref "$revision" | awk '/^Plan ID / {print $3}')
  jq -e --arg commit "$approved_commit" '.commit == $commit' "$CMAKEWORKFLOW_EXPERIMENT_STORE/$other/plan.json"
done

sed -i 's/approved revision/moved branch/' "$git_project/benchmark.c"
git -C "$git_project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qam moved
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
test "$(docker exec "$container" cat "$CMWF_SSHNET_WORKSPACE/experiment-effects")" = $'timing\ntiming'
jq -e --arg commit "$approved_commit" --arg operation "$operation" '
  .commit == $commit and .operationId == $operation and .state == "succeeded"
  and .run.sourceSnapshotId == .sourceSnapshotId
  and .run.environment != null and (.run.buildEnvironments | length) > 0
  and ([.run.nodes[] | select(.status == "succeeded") | .kind] | sort)
    == ["build","configure","ctest","invoke","run","run"]
  and ([.run.nodes[] | select(.kind == "run") | .processDurationSeconds | select(. > 0)] | length) == 2
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
echo "PASS Git experiment: pinned SHA, lost start reply recovered, verified results and benchmark output"
