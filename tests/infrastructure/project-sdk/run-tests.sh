#!/usr/bin/env bash
set -euo pipefail
if (($# == 0)); then
    echo 'Usage: run-tests.sh CMakeWorkflow [CLI arguments such as path-to-dll]' >&2
    exit 2
fi
cli=("$@")
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
lock=$(printf '%064d' 0)
"${cli[@]}" project new library SampleLib "$root/lib" --environment-lock "$lock"
# This name also verifies that the scaffold does not create CTest dashboard targets.
"${cli[@]}" project new application Experimental "$root/app"
if "${cli[@]}" project new application Other "$root/app"; then
    echo 'Existing project was not refused' >&2; exit 1
fi
if "${cli[@]}" project new library '../escape' "$root/invalid"; then
    echo 'Invalid project name was not refused' >&2; exit 1
fi
test ! -e "$root/invalid"
test ! -e "$root/app/workflow.managed.json"
for project in lib app; do
    (cd "$root/$project" && cmake --workflow --preset release)
    compgen -G "$root/$project/out/packages/*.tar.gz" > /dev/null
    "${cli[@]}" workflow run "$root/$project/workflow.json" --results-dir "$root/results-$project" --no-history
done
# Planning must not require a managed compiler installation or invoke CMake.
"${cli[@]}" workflow plan "$root/lib/workflow.managed.json" --json
"${cli[@]}" project sdk workflow "$root/lib" > "$root/lib/resolved.json"
"${cli[@]}" workflow plan "$root/lib/resolved.json" --json
grep -q "$lock" "$root/lib/resolved.json"
cmake --install "$root/lib/out/build/release" --prefix "$root/install"
mkdir "$root/consumer"
cat > "$root/consumer/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.26)
project(consumer LANGUAGES CXX)
find_package(SampleLib 0.1.0 EXACT CONFIG REQUIRED)
add_executable(consumer main.cpp)
target_link_libraries(consumer PRIVATE SampleLib::SampleLib)
CMAKE
cat > "$root/consumer/main.cpp" <<'CPP'
#include <SampleLib/api.hpp>
int main() { return cmwf_example::answer() == 42 ? 0 : 1; }
CPP
cmake -S "$root/consumer" -B "$root/consumer/build" -G Ninja -DCMAKE_PREFIX_PATH="$root/install"
cmake --build "$root/consumer/build"
"$root/consumer/build/consumer"
"${cli[@]}" tooling list --json | grep -q '"packaging"'
echo 'Project SDK smoke tests passed.'
