#!/usr/bin/env bash
set -Eeuo pipefail

cli=$1
scratch=$2
container=$3
version="0.0.0-scriptssh.$(date +%s)"
feed="$scratch/scripting-feed"
local_source="$scratch/controller only"
remote_source="$CMWF_SSHNET_WORKSPACE/script-project"
mkdir -p "$feed" "$local_source"
for package in Contracts Client Scripting; do
    dotnet pack "CMakeWorkflow.$package/CMakeWorkflow.$package.csproj" -c Release \
        -p:Version="$version" -p:TreatWarningsAsErrors=true --artifacts-path "$scratch/scripting-pack" -o "$feed"
done
export RestoreAdditionalProjectSources="$feed"
dotnet pack examples/scripting/NativeFragments/NativeFragments.csproj -c Release \
    -p:ScriptingVersion="$version" -p:Version="$version" -p:TreatWarningsAsErrors=true --artifacts-path "$scratch/scripting-pack" -o "$feed"
sed "s/@0\.1\.0-script\.1/@$version/" examples/scripting/native/workflow.cs > "$local_source/workflow.cs"
docker exec -u cmwf "$container" mkdir -p "$remote_source"
for file in CMakeLists.txt CMakePresets.json main.cpp; do
    docker cp "examples/scripting/native/$file" "$container:$remote_source/$file"
    docker exec "$container" chmod a+r "$remote_source/$file"
done
docker exec "$container" chmod a+rX "$remote_source"

docker exec -i "$container" sh -c 'cat > /opt/cmwf-script-interrupt && chmod 0755 /opt/cmwf-script-interrupt' <<'BRIDGE'
#!/bin/sh
exec dotnet /opt/cmwf-test-peer/CMakeWorkflow.Host.Tests.dll --ssh-drop-start-worker /home/cmwf/private/host.sock /home/cmwf/workspace/script-interrupted interrupt-prepare
BRIDGE
docker exec -i "$container" sh -c 'cat > /opt/cmwf-script-recover && chmod 0755 /opt/cmwf-script-recover' <<'BRIDGE'
#!/bin/sh
marker=/home/cmwf/workspace/script-lost-start
if test -f "$marker"; then
    exec /opt/cmwf/CMakeWorkflow "$@"
fi
exec dotnet /opt/cmwf-test-peer/CMakeWorkflow.Host.Tests.dll --ssh-drop-start-worker /home/cmwf/private/host.sock "$marker"
BRIDGE

connection=(--transport sshnet --host "$CMWF_SSHNET_HOST" --port "$CMWF_SSHNET_PORT"
    --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY" --host-key-sha256 "$CMWF_SSHNET_PIN"
    --endpoint "$CMWF_SSHNET_ENDPOINT" --workspace "$CMWF_SSHNET_WORKSPACE")
options=(--manifest-base script-project --results-dir script-results)
if "$cli" script plan "$local_source/workflow.cs" "${connection[@]}" "${options[@]}" \
    --remote-executable /opt/cmwf-script-interrupt; then
    echo 'Interrupted transfer returned success' >&2
    exit 1
fi
docker exec "$container" test -f "$CMWF_SSHNET_WORKSPACE/script-interrupted"
test -z "$(docker exec "$container" find "$remote_source" -name '.cmwf-workflow-*.json')"
test -z "$(docker exec "$container" find "$CMWF_SSHNET_WORKSPACE" -name '.cmwf-manifest-*.tmp')"

"$cli" script plan "$local_source/workflow.cs" "${connection[@]}" "${options[@]}" \
    --remote-executable "$CMWF_SSHNET_EXE"
docker exec "$container" test ! -d "$remote_source/build"
docker exec "$container" test ! -e "$remote_source/workflow.cs"
"$cli" script run "$local_source/workflow.cs" "${connection[@]}" "${options[@]}" \
    --remote-executable /opt/cmwf-script-recover --approve --request-id script-lost-start | tee "$scratch/script-execution.log"
grep -Fx succeeded "$scratch/script-execution.log"
recorded="$(docker exec "$container" cat "$CMWF_SSHNET_WORKSPACE/script-lost-start")"
found="$("$cli" remote find "${connection[@]}" --remote-executable "$CMWF_SSHNET_EXE" --request-id script-lost-start)"
jq -e --arg operation "$recorded" '.operationId == $operation and .state == "succeeded"' <<< "$found"
docker exec "$container" test -f "$CMWF_SSHNET_WORKSPACE/script-results/run.json"
test "$(docker exec "$container" find "$remote_source" -name '.cmwf-workflow-*.json' | wc -l)" -eq 1
echo 'PASS actual SSH manifest transfer, no partial publication, durable recovery and native configure/build/CTest'
