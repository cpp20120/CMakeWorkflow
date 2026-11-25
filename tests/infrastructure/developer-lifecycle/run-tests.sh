#!/usr/bin/env bash
set -euo pipefail

cli=$1
consumer=$2
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
fixture=$(cd "$fixture" && pwd -P)
if [[ "${RUNNER_OS:-}" == Windows ]]; then
    fixture=$(cygpath -am "$fixture")
fi
project="$fixture/Runtime"

"$consumer"
"$cli" new runtime Runtime "$project" --json > "$fixture/new.json"
test ! -d "$project/out"
"$cli" test --project "$project" --plan --json > "$fixture/plan.json"
test ! -d "$project/out"
api_digest=$("$consumer" --plan "$project" | tr -d '\r')
python - "$fixture/plan.json" "$api_digest" <<'PY'
import json
import pathlib
import re
import sys

plan = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
assert plan["status"] == "planned"
assert plan["manifestSha256"] == sys.argv[2]
assert re.fullmatch(r"[a-f0-9]{64}", sys.argv[2])
assert [node["kind"] for node in plan["manifest"]["nodes"]] == ["configure", "invoke", "ctest"]
PY

"$cli" doctor --project "$project" --json > "$fixture/doctor.json"
"$cli" restore --project "$project"
"$cli" build --project "$project" --no-history
"$cli" test --project "$project" --history-db "$project/out/history.sqlite" --json > "$fixture/test.json"
"$cli" benchmark --project "$project" --no-history --repeats 3
"$cli" pack --project "$project" --no-history
python - "$fixture/test.json" "$project" <<'PY'
import json
import pathlib
import sys
import zipfile

result = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
assert result["status"] == "succeeded"
assert result["operationId"] and result["reportSha256"]
assert result["history"]["state"] == "indexed", result["history"]
packages = list(pathlib.Path(sys.argv[2]).glob("out/build/*/cmwf-package.zip"))
assert len(packages) == 1
with zipfile.ZipFile(packages[0]) as archive:
    assert any(name.endswith("/RuntimeConfig.cmake") for name in archive.namelist())
PY

mkdir "$fixture/consumer"
python - "$project" "$fixture/consumer" <<'PY'
import pathlib
import sys
import zipfile

archive = next(pathlib.Path(sys.argv[1]).glob("out/build/*/cmwf-package.zip"))
with zipfile.ZipFile(archive) as package:
    package.extractall(sys.argv[2])
PY
cat > "$fixture/consumer/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(InstalledRuntime LANGUAGES CXX)
enable_testing()
find_package(Runtime CONFIG REQUIRED)
add_executable(installed_runtime main.cpp)
target_link_libraries(installed_runtime PRIVATE Runtime::Runtime)
add_test(NAME installed_runtime COMMAND installed_runtime)
CMAKE
cat > "$fixture/consumer/main.cpp" <<'CPP'
#include <Runtime/queue.hpp>
#include <cstdint>

int main() {
    cmwf_runtime::Queue queue;
    std::uint32_t value{};
    return queue.push(42) && queue.pop(value) && value == 42 ? 0 : 1;
}
CPP
cmake -S "$fixture/consumer" -B "$fixture/consumer/build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    "-DCMAKE_PREFIX_PATH=$fixture/consumer/cmwf-package"
cmake --build "$fixture/consumer/build"
ctest --test-dir "$fixture/consumer/build" --output-on-failure
