#!/usr/bin/env bash
set -Eeuo pipefail

# Run against an isolated SSH.NET test Host with the linux-clang profile already provisioned.
cli=${1:?Usage: experiment-upstream.sh /path/to/CMakeWorkflow}
: "${CMAKEWORKFLOW_EXPERIMENT_STORE:?Set a controller evidence directory}"
repo=https://github.com/fmtlib/fmt.git
commit=40626af88bd7df9a5fb80be7b25ac85b122d6c21 # fmt 11.2.0
plan=$("$cli" experiment plan --repo "$repo" --ref "$commit" \
  --host "$CMWF_SSHNET_HOST" --workspace "$CMWF_SSHNET_WORKSPACE" --endpoint "$CMWF_SSHNET_ENDPOINT" \
  --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY" --host-key-sha256 "$CMWF_SSHNET_PIN" \
  --port "$CMWF_SSHNET_PORT" --remote-executable "$CMWF_SSHNET_EXE" \
  --managed-clang --config Release --benchmark-target format-test --repeats 3 | awk '/^Plan ID / {print $3}')
plan_dir=$CMAKEWORKFLOW_EXPERIMENT_STORE/$plan
result=$plan_dir/result.json
if ! "$cli" experiment run --plan "$plan" --approve --wait-seconds 600; then
  if [[ -f "$result" ]]; then jq '.run.nodes[] | {id, status, error}' "$result" >&2; fi
  exit 1
fi
jq -e --arg commit "$commit" '
  .commit == $commit and .state == "succeeded"
  and ([.run.nodes[] | select(.id == "ctest" and .status == "succeeded")] | length) == 1
  and ([.run.nodes[] | select(.kind == "run" and .processDurationSeconds > 0)] | length) == 3
' "$result"
artifact=$(jq -er '.artifacts[] | select(.nodeId == "ctest" and .kind == "stdout") | .artifactId' "$result")
if [[ ! -f "$plan_dir/upstream-ctest.stdout" ]]; then
  "$cli" experiment download "$plan" --artifact "$artifact" --output "$plan_dir/upstream-ctest.stdout"
fi
grep -F '100% tests passed' "$plan_dir/upstream-ctest.stdout"
test -s "$plan_dir/report.html"
