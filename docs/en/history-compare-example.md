# Compare two C++ builds in History

**[English](history-compare-example.md) · [Русский](../ru/history-compare-example.md)**

Does selecting the median with `std::nth_element` make this program faster than sorting the entire input?
[The example](../../examples/history-compare/main.cpp) compiles both implementations from the same source. Each
process generates the same data and prints the same median.

From the repository root, use CMake 3.27+, Ninja, a C++17 compiler and a
[release binary](https://github.com/cpp20120/CMakeWorkflow/releases) on your PATH. You can also substitute
`dotnet bin/Release/net10.0/CMakeWorkflow.dll` after [building the CLI](getting-started.md#build-and-plan).

## Run the baseline

```sh
cmake -S examples/history-compare -B out/history-compare/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DUSE_NTH_ELEMENT=OFF
CMakeWorkflow workflow generate benchmark --build-dir out/history-compare/build --target median_bench --config Release --repeats 10 --timeout-seconds 30 --output out/history-compare/benchmark.json
CMakeWorkflow workflow run out/history-compare/benchmark.json --results-dir out/history-compare/baseline --history-db out/history-compare/history.sqlite
```

CMake configures the tree and emits the target metadata via its File API. The generated workflow builds
`median_bench` and runs it ten times in sequence. On reruns, skip the generation command and reuse `benchmark.json`.

## Run the candidate

```sh
cmake -S examples/history-compare -B out/history-compare/build -DUSE_NTH_ELEMENT=ON
CMakeWorkflow workflow run out/history-compare/benchmark.json --results-dir out/history-compare/candidate --history-db out/history-compare/history.sqlite
```

CMake rebuilds the target with the other implementation. Run both steps on the same machine, with the same compiler
and Release configuration. Using the same manifest keeps the workload and timing method identical.

## Compare and inspect

```sh
CMakeWorkflow history runs --db out/history-compare/history.sqlite --status succeeded --limit 2
CMakeWorkflow history compare BASELINE_ID CANDIDATE_ID --db out/history-compare/history.sqlite
```

Copy the IDs from the first column and match them to the `baseline` and `candidate` paths in the last column.
`process_wall_time` includes data generation and process startup. A negative median delta means the candidate took
less time. In one local run the median went from 55.4 ms to 10.6 ms. If History marks the row `unverified` or
`incompatible`, read its reason. It omits the delta in either case.
See [comparison and regression detection](results-and-history.md#comparable-measurements-versus-regressions) for how
to interpret the result.

Open a run with `CMakeWorkflow workflow report RUN_DIRECTORY --json`, using the directory printed by
`history runs`. The saved `run.json` contains node status and log paths. Check the `stdout` log for `timing_001`
in each run to see that both implementations returned the same input median, `2149777886`.
