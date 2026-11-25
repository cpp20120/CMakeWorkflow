#!/usr/bin/env bash
set -euo pipefail

cli=$1
consumer=$2
container=$3
root=$4
local_project="$root/kit-project"
remote_project="$CMWF_SSHNET_WORKSPACE/kit-project"

"$cli" new runtime RemoteKit "$local_project"
kit=$("$cli" kit plan linux-gcc --project "$local_project" | jq -er .kitLockSha256)
"$cli" kit lock linux-gcc --project "$local_project" --plan-sha "$kit"
configuration=$("$cli" restore --project "$local_project" --kit linux-gcc --plan --json | jq -er .configurationLockSha256)
"$cli" restore --project "$local_project" --kit linux-gcc --configuration-plan-sha "$configuration"
docker cp "$local_project" "$container:$remote_project"
docker exec "$container" chown -R cmwf:cmwf "$remote_project"
docker exec --user cmwf "$container" /opt/cmwf/CMakeWorkflow kit use linux-clang --project "$remote_project"
"$consumer" --kit-remote "$local_project" "$remote_project" accepted-kit

lock="$remote_project/cmwf-kits/$kit/lock.json"
docker exec --user cmwf "$container" mv "$lock" "$lock.original"
if "$consumer" --kit-remote "$local_project" "$remote_project" missing-kit > "$root/missing-kit.log" 2>&1; then
    exit 1
fi
grep -E 'lock\.json|No such file' "$root/missing-kit.log"
docker exec --user cmwf "$container" sh -c 'cat "$1.original" >"$1"; printf "\n" >>"$1"' sh "$lock"
if "$consumer" --kit-remote "$local_project" "$remote_project" modified-kit > "$root/modified-kit.log" 2>&1; then
    exit 1
fi
grep -F kit-lock-changed "$root/modified-kit.log"
