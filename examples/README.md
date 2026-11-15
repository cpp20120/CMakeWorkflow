# Example workflows

Run the commands from the repository root with the CMakeWorkflow executable on PATH. See the
[quickstart](../docs/en/getting-started.md) to build it from source. The examples use Ninja and a C++ compiler.

| Manifest | Use case |
| --- | --- |
| [hello/workflow.json](hello/workflow.json) | Configure, build, run, and test a small executable |
| [test-matrix.json](hello/workflows/test-matrix.json) | Run labeled CTest tests against separate Debug and Release build trees |
| [artifact-handoff.json](hello/workflows/artifact-handoff.json) | Run a CMake utility target and pass its verified output to an executable |
| [inspect.json](history-compare/workflows/inspect.json) | Inspect an existing CMake build tree from a workflow |
| [benchmark.json](history-compare/workflows/benchmark.json) | Collect three sequential process timings from an existing Release build tree |
| [reproducible-run.json](hello/workflows/reproducible-run.json) | Pin an input by SHA-256 and save a snapshot with the run |
| [shared-resource.json](hello/workflows/shared-resource.json) | Serialize two measurements while an independent CTest task runs in parallel |
| [profile-and-time.json](history-compare/workflows/profile-and-time.json) | Record process wall time and a separate CPU profile from the same executable |
| [recover-interrupted.json](hello/workflows/recover-interrupted.json) | Interrupt a runner after a side effect and inspect the resulting checkpoint |

## Test Debug and Release

```sh
CMakeWorkflow workflow plan examples/hello/workflows/test-matrix.json
CMakeWorkflow workflow run examples/hello/workflows/test-matrix.json --jobs 2 --results-dir out/workflow-examples/test-matrix --no-history
```

The two branches use separate build trees, so they can run concurrently. Add `--only test-release` to run
only the Release test and its configure and build prerequisites.

## Pass an artifact between nodes

```sh
CMakeWorkflow workflow run examples/hello/workflows/artifact-handoff.json --results-dir out/workflow-examples/artifact-handoff --no-history
```

`stage-message` invokes the CMake utility target `stage_message` and records the SHA-256 of its output.
`read-message` verifies that artifact as a declared input and runs `hello`, which reads the file.
Its stdout contains `hello from a declared workflow artifact`. Find the log and recorded digest in the
saved `run.json` under the directory printed as `Results:`.

## Inspect or benchmark an existing build tree

Configure the benchmark project with CMake 3.27 or newer. It emits the File API codemodel during configure.

```sh
cmake -S examples/history-compare -B out/history-compare/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DUSE_NTH_ELEMENT=OFF
CMakeWorkflow workflow run examples/history-compare/workflows/inspect.json --results-dir out/workflow-examples/inspect --no-history
CMakeWorkflow workflow run examples/history-compare/workflows/benchmark.json --results-dir out/workflow-examples/benchmark --history-db out/history-compare/quick-history.sqlite
CMakeWorkflow history runs --db out/history-compare/quick-history.sqlite
```

`inspect.json` records what CMakeWorkflow sees without configuring or building the project.
`benchmark.json` adopts the configured tree, builds the target and collects three wall-time samples.
For a two-build comparison with ten samples per build, see [Compare two C++ builds in History](../docs/en/history-compare-example.md)
or [Сравнение двух C++-сборок в History](../docs/ru/history-compare-example.md).

## Pin an input and reject a changed copy

```sh
cmake -E make_directory out/workflow-examples/inputs
cmake -E copy examples/hello/message.txt out/workflow-examples/inputs/message.txt
CMakeWorkflow workflow run examples/hello/workflows/reproducible-run.json --results-dir out/workflow-examples/pinned --no-history
```

In the saved `run.json`, find the SHA-256 and `snapshotPath` under `declaredArtifacts` for `read-pinned-message`.
Now overwrite the disposable input copy and run the same manifest again.

```sh
cmake -E echo "changed input" > out/workflow-examples/inputs/message.txt
CMakeWorkflow workflow run examples/hello/workflows/reproducible-run.json --results-dir out/workflow-examples/tampered --no-history
```

The second command exits with code 1. The report shows a digest mismatch before the executable reads the file.
Copy the original fixture again to repeat the successful run.

## Share a scheduler resource

```sh
CMakeWorkflow workflow run examples/hello/workflows/shared-resource.json --jobs 3 --results-dir out/workflow-examples/shared-resource --no-history
```

The measurement branches use distinct build trees and the same `measurement-host` resource. In `events.jsonl`,
one measurement finishes before the other starts, while `independent-test` overlaps a measurement. The example
uses short, timed waits to make the scheduling order visible.

## Time and profile the same executable

Use the same CMake configure command as in [the benchmark example above](#inspect-or-benchmark-an-existing-build-tree).
The CPU capture requires a usable platform profiler and permission to collect events.

```sh
CMakeWorkflow workflow run examples/history-compare/workflows/profile-and-time.json --results-dir out/workflow-examples/profile --no-history
```

`timing_001` records ordinary process wall time. The exclusive `profile-cpu` node then captures the same executable
using the host's CPU profiling backend. Open `run.json` for node results and `analysis.json` for the saved profile analysis.
On Linux, `constraints.cpuSet` can pin either node to a CPU allowed by `taskset -pc $$`. Apply the same CPU set to
both nodes if you want the timing and capture to use the same affinity.

## Inspect recovery after interrupting a runner

Run the demo from the repository root on Linux with Bash. It starts its own runner, waits for the example
executable to append `started` to a test log, then sends SIGKILL to that runner. The native child finishes its
short wait and appends `finished`.

```sh
bash examples/hello/workflows/run-recovery-demo.sh
```

For the source-build CLI, pass `dotnet bin/Release/net10.0/CMakeWorkflow.dll` as arguments to the script.
The first `workflow resume` exits with code 4 and records `outcome_unknown`. The script prints the
saved report and checks that the side-effect log contains exactly one `started` entry.
See [Recovery](../docs/en/recovery.md) for inspecting and reconciling an actual interrupted run.
