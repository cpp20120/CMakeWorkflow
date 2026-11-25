#!/usr/bin/env bash
set -euo pipefail
cli=$1
workspace=$(mktemp -d)
trap 'rm -rf "$workspace"' EXIT
project="$workspace/project"
"$cli" new runtime Campaign "$project"
for kit in linux-gcc linux-system-clang; do
  kit_sha=$("$cli" kit plan "$kit" --project "$project" | jq -er .kitLockSha256)
  "$cli" kit lock "$kit" --project "$project" --plan-sha "$kit_sha"
  config_sha=$("$cli" restore --project "$project" --kit "$kit" --profile release --plan --json | jq -er .configurationLockSha256)
  "$cli" restore --project "$project" --kit "$kit" --profile release --configuration-plan-sha "$config_sha"
done
review=$(dotnet run -c Release --file examples/engineering/campaign.cs -- "$project" campaign-e2e)
sha=$(awk '/^Manifest SHA-256 / { print $3 }' <<< "$review")
[[ "$sha" =~ ^[a-f0-9]{64}$ ]]
[[ ! -d "$project/out" ]]
status=0
dotnet run -c Release --file examples/engineering/campaign.cs -- "$project" campaign-e2e "$sha" this kit-00-release kit-01-release > "$workspace/results.log" 2>&1 || status=$?
cat "$workspace/results.log"
grep -E '^Operation [a-f0-9]{32} History run [a-f0-9]{64}$' "$workspace/results.log"
grep -q '^kit-00-release n=5 ' "$workspace/results.log"
grep -q '^kit-01-release n=5 ' "$workspace/results.log"
case "$status" in
  0) grep -q '^Gate Passed ' "$workspace/results.log" ;;
  1) grep -q '^Gate Failed ' "$workspace/results.log" ;;
  *) exit "$status" ;;
esac
