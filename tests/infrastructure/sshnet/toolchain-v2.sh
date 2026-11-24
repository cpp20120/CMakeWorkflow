#!/usr/bin/env bash
set -Eeuo pipefail
cli=$1
container=$2
host_cli=(docker exec --user cmwf "$container" "$CMWF_SSHNET_EXE")
remote_options=(--transport sshnet --host "$CMWF_SSHNET_HOST" --user "$CMWF_SSHNET_USER"
  --key-file "$CMWF_SSHNET_KEY" --host-key-sha256 "$CMWF_SSHNET_PIN" --port "$CMWF_SSHNET_PORT"
  --workspace "$CMWF_SSHNET_WORKSPACE" --endpoint "$CMWF_SSHNET_ENDPOINT" --remote-executable "$CMWF_SSHNET_EXE")
project="$CMWF_SSHNET_WORKSPACE/sdk-v2"
sha=$("${host_cli[@]}" tooling resolve --profile linux-clang | jq -er '.environmentSha256')
"${host_cli[@]}" tooling lock --profile linux-clang --approve-sha "$sha"
"${host_cli[@]}" project new application ManagedV2 "$project" --environment-lock "$sha"
manifest="$project/workflow.managed.json"
manifest_sha=$(docker exec --user cmwf "$container" sha256sum "$manifest" | awk '{print $1}')
operation=$("$cli" remote start "${remote_options[@]}" --request-id sdk-v2 --manifest "$manifest" \
    --manifest-sha256 "$manifest_sha" --results-dir "$project/results" | jq -er '.operationId')
for ((attempt=0; attempt<45; attempt++)); do
    state=$("$cli" remote status "${remote_options[@]}" --operation "$operation" | jq -er '.state')
    case "$state" in
        succeeded) break ;;
        failed|canceled) echo "Remote v2 run ended in $state" >&2; exit 1 ;;
        *) sleep 1 ;;
    esac
done
test "$state" = succeeded
docker exec --user cmwf "$container" cat "$project/results/run.json" |
    jq -e --arg sha "$sha" 'any(.toolchains[]; .linux.environmentLockSha256 == $sha) and
        any(.nodes[]; .kind == "ctest" and .status == "succeeded")'
echo 'PASS v2 environment lock admitted by persistent Host over SSH.NET'
