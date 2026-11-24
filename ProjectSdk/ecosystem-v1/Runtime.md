# @NAME@

The bounded queue owns 64 entries and reports full and empty states explicitly.
The `CMWF_RUNTIME_THREADS` CMake option enables internal mutex protection.
With the option off, callers synchronize access to each queue.

```sh
cmake --workflow --preset debug
cmake --workflow --preset release
cmakeworkflow workflow generate benchmark --build-dir out/build/release --source-dir . --preset release --target @NAME@_bench --config Release --repeats 3 --timeout-seconds 30 --output benchmark.json -- 100000
cmakeworkflow workflow run benchmark.json --results-dir out/bench
cmakeworkflow workflow run benchmark.json --results-dir out/bench
cmakeworkflow history runs --status succeeded --limit 2
cmakeworkflow history compare BASELINE_ID CANDIDATE_ID
cmakeworkflow workflow generate profile --build-dir out/build/release --source-dir . --preset release --target @NAME@_bench --config Release --platform linux --backend auto --mode cpu --repeats 3 --timeout-seconds 30 --output profile.json -- 100000
cmakeworkflow workflow run profile.json --results-dir out/profile
```

Use the IDs printed by History in the comparison command. Profile execution
requires the platform profiling backend.

`cmwf.config.json` includes a Debug/Release static/shared matrix and a Linux
GCC `asan` profile with address and undefined behavior instrumentation.
