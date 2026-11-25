#!/usr/bin/env bash
set -euo pipefail

cli=$1
consumer=$2
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
project="$tmp/Recovery"

"$cli" new runtime KitRecovery "$project"
kit=$("$cli" kit plan linux-gcc --project "$project" | jq -er .kitLockSha256)
"$cli" kit lock linux-gcc --project "$project" --plan-sha "$kit"
configuration=$("$cli" restore --project "$project" --kit linux-gcc --plan --json | jq -er .configurationLockSha256)
"$cli" restore --project "$project" --kit linux-gcc --configuration-plan-sha "$configuration"
CMAKEWORKFLOW_TEST_FAULT=before-dispatch CMAKEWORKFLOW_TEST_NODE=release-build CMAKEWORKFLOW_TEST_MARKER="$tmp/ready" \
  "$cli" build --project "$project" --kit linux-gcc --no-history > "$tmp/run.log" 2>&1 &
process=$!
for (( attempt=0; attempt<150; attempt++ )); do
  if test -s "$tmp/ready"; then break; fi
  kill -0 "$process" || { cat "$tmp/run.log"; exit 1; }
  sleep .2
done
if ! test -s "$tmp/ready"; then
  kill -TERM "$process"
  wait "$process" || true
  cat "$tmp/run.log"
  exit 1
fi
kill -KILL "$process"
wait "$process" 2>/dev/null || true
operation=$(awk '/^Operation ID / {print $3}' "$tmp/run.log")
test -n "$operation"
"$cli" kit use linux-system-clang --project "$project"
checkpoint=$(find "$project/out/results" -name run.json -print -quit)
run=$(dirname "$checkpoint")
resume_status=0
"$cli" workflow resume "$run" || resume_status=$?
test "$resume_status" = 4
checkpoint_sha=$(sha256sum "$checkpoint" | awk '{print $1}')
node=()
if jq -e '.nodes[] | select(.id == "release-build" and .status == "outcome_unknown")' "$checkpoint" >/dev/null; then
  node=(--node release-build)
fi
"$cli" workflow reconcile "$run" --decision retry --reason "Stopped before dispatch at the recovery test barrier" \
  --expected-checkpoint "$checkpoint_sha" --confirm-stopped "${node[@]}"
"$consumer" --kit-recover "$project" "$operation"
