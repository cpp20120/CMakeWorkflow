#!/usr/bin/env bash
set -euo pipefail

cli=$1
scratch=$2
container=$3
workspace=$CMWF_SSHNET_WORKSPACE
project="$workspace/format-project"
connection=(--transport sshnet --host "$CMWF_SSHNET_HOST" --port "$CMWF_SSHNET_PORT"
    --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY" --host-key-sha256 "$CMWF_SSHNET_PIN"
    --endpoint "$CMWF_SSHNET_ENDPOINT" --workspace "$workspace" --remote-executable "$CMWF_SSHNET_EXE")

approval=$(docker exec --user cmwf "$container" "$CMWF_SSHNET_EXE" tooling plan     --profile cmakefmt --tool-policy ManagedOnly --json 2>&1 | awk '/^Plan SHA-256: / {print $3}')
[[ $approval =~ ^[a-f0-9]{64}$ ]]
docker exec --user cmwf "$container" "$CMWF_SSHNET_EXE" tooling setup     --profile cmakefmt --tool-policy ManagedOnly --yes --plan-sha "$approval"

docker exec --user cmwf "$container" mkdir -p "$project/cmake"
docker cp examples/scripting/format/CMakeLists.txt "$container:$project/CMakeLists.txt"
docker cp examples/scripting/format/CMakePresets.json "$container:$project/CMakePresets.json"
docker cp examples/scripting/format/smoke.cpp "$container:$project/smoke.cpp"
docker cp examples/scripting/format/cmake/Tests.cmake "$container:$project/cmake/Tests.cmake"
docker exec "$container" chown -R cmwf:cmwf "$project"

manifest="$scratch/format-workflow.json"
cat > "$manifest" <<'JSON'
{
  "schemaVersion": 2,
  "contexts": {"native": {"sourceDirectory": ".", "buildDirectory": "build", "configurePreset": "ninja"}},
  "nodes": [
    {"id": "format", "kind": "format_check", "context": "native", "arguments": ["cmake", "managed"]},
    {"id": "configure", "kind": "configure", "context": "native", "dependsOn": ["format"]},
    {"id": "build", "kind": "build", "context": "native", "target": "smoke", "dependsOn": ["configure"]},
    {"id": "test", "kind": "ctest", "context": "native", "dependsOn": ["build"]}
  ]
}
JSON
docker cp "$manifest" "$container:$project/workflow.json"
docker exec "$container" chmod a+r "$project/workflow.json"
digest=$(sha256sum "$manifest" | cut -d ' ' -f 1)
printf '\nset( FORMAT_FIXTURE yes )\n' | docker exec -i --user cmwf "$container"     sh -c 'cat >> "$1"' sh "$project/cmake/Tests.cmake"

run_gate() {
    local expected=$1 status operation
    operation=$("$cli" remote start "${connection[@]}" --request-id "format-$expected" --manifest "$project/workflow.json"         --manifest-sha256 "$digest" --results-dir "$workspace/format-$expected" --no-history | jq -er .operationId)
    for ((i=0; i<60; ++i)); do
        status=$("$cli" remote status "${connection[@]}" --operation "$operation" | jq -er .state)
        case "$status" in
            succeeded|failed|canceled|outcome_unknown) break ;;
        esac
        sleep 1
    done
    if [[ $status != "$expected" ]]; then
        "$cli" remote report "${connection[@]}" --operation "$operation" >&2
        docker exec "$container" sh -c 'find "$1" -type f -name "*.stderr.log" -exec cat {} +'             sh "$workspace/format-$expected" >&2
        return 1
    fi
    "$cli" remote download "${connection[@]}" --operation "$operation" --artifact-id stdout:format         --output "$scratch/format-$expected.json"
}

run_gate failed
jq -e '.status == "FormattingRequired"' "$scratch/format-failed.json"
docker exec "$container" test ! -e "$project/build/CMakeCache.txt"
docker cp examples/scripting/format/cmake/Tests.cmake "$container:$project/cmake/Tests.cmake"
docker exec "$container" chmod a+r "$project/cmake/Tests.cmake"
run_gate succeeded
jq -e '.status == "Clean" and .tools[0].origin == "managed"' "$scratch/format-succeeded.json"
echo 'PASS remote formatting blocks configure and publishes a downloadable report'
