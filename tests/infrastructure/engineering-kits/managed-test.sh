#!/usr/bin/env bash
set -euo pipefail

cli=$1
consumer=$2
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
export CMAKEWORKFLOW_TOOL_STORE="$tmp/store"
export XDG_CACHE_HOME="$tmp/cache"
export VCPKG_BINARY_SOURCES=clear

profiles=(--profile linux-clang --profile vcpkg)
"$cli" tooling plan "${profiles[@]}" --tool-policy ManagedOnly --json > "$tmp/tools.json" 2> "$tmp/tools.approval"
approval=$(sed -n 's/^Plan SHA-256: //p' "$tmp/tools.approval")
"$cli" tooling setup "${profiles[@]}" --tool-policy ManagedOnly --yes --plan-sha "$approval"

for generator in Ninja 'Ninja Multi-Config'; do
    sha=$("$cli" tooling resolve --profile linux-clang --generator "$generator" | jq -er 'select(.ready).environmentSha256')
    "$cli" tooling lock --profile linux-clang --generator "$generator" --approve-sha "$sha"
done
bash "$(dirname "$0")/run-tests.sh" "$cli" "$consumer" linux-clang linux-clang-multi
bash "$(dirname "$0")/vcpkg-test.sh" "$cli"
