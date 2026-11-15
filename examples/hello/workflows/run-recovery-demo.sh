#!/usr/bin/env bash
set -euo pipefail

cli=("$@")
if (( ${#cli[@]} == 0 )); then
    cli=(CMakeWorkflow)
fi

marker=out/hello-demo/attempts.log
if [[ -e "$marker" || -L "$marker" ]]; then
    echo "Move $marker aside after inspecting it before starting a fresh demo." >&2
    exit 1
fi

mkdir -p out/workflow-examples
demo=$(mktemp -d out/workflow-examples/recovery.XXXXXX)
log="$demo/runner.log"
"${cli[@]}" workflow run examples/hello/workflows/recover-interrupted.json \
    --results-dir "$demo" --no-history > "$log" 2>&1 &
runner=$!
trap 'if [[ -n "$runner" ]]; then kill "$runner" 2>/dev/null || true; wait "$runner" 2>/dev/null || true; fi' EXIT

deadline=$((SECONDS + 60))
until grep -qsx started "$marker"; do
    if (( SECONDS >= deadline )) || ! kill -0 "$runner" 2>/dev/null; then
        echo "The demo did not reach its side effect. See $log." >&2
        exit 1
    fi
    sleep 0.1
done

kill -KILL "$runner"
wait "$runner" 2>/dev/null || true
runner=

# The native child can outlive the runner. This demo child writes its final line and exits.
deadline=$((SECONDS + 15))
until grep -qsx finished "$marker"; do
    if (( SECONDS >= deadline )); then
        echo "The child did not record completion. See $marker and $log." >&2
        exit 1
    fi
    sleep 0.1
done

run_dir=$(sed -n 's/^\[CMakeWorkflow\] Results: //p' "$log")
test -f "$run_dir/run.json"
resume_status=0
"${cli[@]}" workflow resume "$run_dir" || resume_status=$?
if [[ "$resume_status" != 4 || $(grep -cx started "$marker") != 1 ]]; then
    echo "Expected outcome_unknown (exit 4) and exactly one recorded launch. See $marker and $run_dir/run.json." >&2
    exit 1
fi

mv "$marker" "$demo/attempts.log"
"${cli[@]}" workflow report "$run_dir"
echo "Recorded side effects: $demo/attempts.log"
