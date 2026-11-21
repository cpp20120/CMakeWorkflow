#!/usr/bin/env bash
set -Eeuo pipefail
# Run on the SSH fixture after provisioning linux-clang and vcpkg.
cli=$1
project=$2
upstream=a0d572ea1d8d62c0154ca82d65397bd4cec195dc
repo=https://github.com/doanhtu07/hello-world-vcpkg.git
git init -q "$project"
for attempt in 1 2 3; do
  if git -C "$project" -c http.version=HTTP/1.1 fetch --depth 1 "$repo" "$upstream"; then break; fi
  if ((attempt == 3)); then exit 1; fi
  sleep "$attempt"
done
git -C "$project" checkout -q --detach FETCH_HEAD
test "$(git -C "$project" rev-parse HEAD)" = "$upstream"

# Adapt registry policy, disable the optional copy of an untracked resources directory,
# and register the existing executable with CTest.
# The original application and its fmt dependency are unchanged.
baseline=84bab45d415d22042bd0b9081aea57f362da3f35
jq -n --arg sha "$baseline" '{ "default-registry":{
  kind:"git", repository:"https://github.com/microsoft/vcpkg", baseline:$sha, reference:$sha
}}' > "$project/vcpkg-configuration.json"
sed -i '/^if(PROJECT_IS_TOP_LEVEL AND UNIX)/,$d' "$project/CMakeLists.txt"
sed -i 's/option(PRODUCTION_BUILD "Make this a production build" ON)/option(PRODUCTION_BUILD "Make this a production build" OFF)/' "$project/CMakeLists.txt"
printf '\nenable_testing()\nadd_test(NAME fmt_consumer COMMAND HelloWorld)\n' >> "$project/CMakeLists.txt"
GIT_AUTHOR_DATE=2025-12-12T00:00:00Z GIT_COMMITTER_DATE=2025-12-12T00:00:00Z \
  git -C "$project" -c user.name=Fixture -c user.email=fixture@example.invalid commit -qam "Pin registry and add CTest smoke"
commit=$(git -C "$project" rev-parse HEAD)
plan=$("$cli" experiment plan --repo "file://$project" --ref "$commit" \
  --host "$CMWF_SSHNET_HOST" --workspace "$CMWF_SSHNET_WORKSPACE" --endpoint "$CMWF_SSHNET_ENDPOINT" \
  --user "$CMWF_SSHNET_USER" --key-file "$CMWF_SSHNET_KEY" --host-key-sha256 "$CMWF_SSHNET_PIN" \
  --port "$CMWF_SSHNET_PORT" --remote-executable "$CMWF_SSHNET_EXE" \
  --managed-clang --config Release --vcpkg --benchmark-target HelloWorld --repeats 3 | awk '/^Plan ID / {print $3}')
dir=$CMAKEWORKFLOW_EXPERIMENT_STORE/$plan
"$cli" experiment deps plan "$plan" > "$dir/dependencies.log"
sha=$(awk '/^Dependency plan SHA-256 / {print $4}' "$dir/dependencies.log")
"$cli" experiment deps apply "$plan" --approve --plan-sha "$sha"
if ! "$cli" experiment run --plan "$plan" --approve --wait-seconds 120; then
  if [[ -f $dir/result.json ]]; then
    while IFS= read -r node; do
      "$cli" experiment download "$plan" --artifact "stdout:$node" --output "$dir/$node.stdout"
      cat "$dir/$node.stdout" >&2
    done < <(jq -r '.run.nodes[] | select(.status == "failed") | .id' "$dir/result.json")
  fi
  exit 1
fi
jq -e --arg sha "$sha" '
  .state == "succeeded" and any(.run.toolchains[]; .linux.vcpkgProjectSha256 == $sha and .linux.vcpkgPackageTreeSha256 != null)
  and any(.run.nodes[]; .kind == "ctest" and .status == "succeeded")
  and ([.run.nodes[] | select(.kind == "run" and .status == "succeeded")] | length) == 3
' "$dir/result.json"
artifact=$(jq -er '.artifacts[] | select(.nodeId == "timing_001" and .kind == "stdout") | .artifactId' "$dir/result.json")
"$cli" experiment download "$plan" --artifact "$artifact" --output "$dir/benchmark.stdout"
grep -F "Hello World!" "$dir/benchmark.stdout"
test -s "$dir/report.html"
