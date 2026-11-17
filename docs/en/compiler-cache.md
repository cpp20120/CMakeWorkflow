# Compiler cache profiles

Configure a profile on the machine that runs CMake. The standalone CLI reads its local registry. For a persistent
SSH Host, set `CMAKEWORKFLOW_COMPILER_CACHE_CONFIG` in the remote Host service's environment. Use an absolute
registry path in both cases. A manifest or recipe selects a profile by name:

```json
{
  "profiles": {
    "local-ccache": {
      "provider": "ccache",
      "executable": "/usr/bin/ccache",
      "configurationFile": "/home/builder/cache/ccache.conf"
    },
    "team-sccache": {
      "provider": "sccache",
      "executable": "/usr/bin/sccache",
      "configurationFile": "/home/builder/cache/sccache.toml",
      "serverSocket": "/home/builder/cache/sccache.sock"
    }
  }
}
```

The executable and native configuration file must exist on the execution host. Use the provider's native configuration
for local directories and remote backends. For example, ccache accepts `cache_dir` and `remote_storage`; sccache
accepts sections such as `[cache.disk]` and `[cache.s3]`. The Host passes `CCACHE_CONFIGPATH` or `SCCACHE_CONF` to its
child processes. Keep credentials in the provider's environment or private configuration. Use `serverSocket` on Unix
or `serverPort` where a separate sccache endpoint is needed. Set the provider configuration when starting an existing
sccache daemon. If changing a daemon's flags or backend through external settings, update the optional profile
`revision` and restart the daemon through its operator.

## Cold and warm builds

For a local ccache demo, from the repository root on a Unix host with Ninja and ccache installed:

```sh
cache_demo=$(mktemp -d "${TMPDIR:-/tmp}/cmw-cache.XXXXXX")
printf 'cache_dir = %s/cache\n' "$cache_demo" > "$cache_demo/ccache.conf"
cat > "$cache_demo/profiles.json" <<EOF
{"profiles":{"local-ccache":{"provider":"ccache","executable":"$(command -v ccache)","configurationFile":"$cache_demo/ccache.conf"}}}
EOF
export CMAKEWORKFLOW_COMPILER_CACHE_CONFIG="$cache_demo/profiles.json"

for pass in cold warm; do
  CMakeWorkflow workflow generate benchmark --source-dir examples/hello --preset release \
    --build-dir "$cache_demo/$pass" --target hello --config Release --repeats 2 --timeout-seconds 30 \
    --compiler-cache-profile local-ccache --output "$cache_demo/$pass.json"
  CMakeWorkflow workflow run "$cache_demo/$pass.json" --results-dir "$cache_demo/results/$pass" \
    --history-db "$cache_demo/history.sqlite"
done

CMakeWorkflow history runs --db "$cache_demo/history.sqlite"
CMakeWorkflow history query --metric compiler_cache_hits --db "$cache_demo/history.sqlite"
CMakeWorkflow history query --metric build_wall_time --db "$cache_demo/history.sqlite"
```

The new build directory forces compilation on both passes. With an ordinary Release C++ target, the first pass
should record a miss and the second a hit. Use the reported run IDs to query the build metrics and
`process_wall_time` for the two timings.

`compilerCacheProfile` also works on a workflow context, a Benchmark recipe and variants in Build Matrix or
Benchmark Matrix recipes. A compatible Host advertises `workflow/compilerCacheProfile`. A managed configure
supplies absolute C and C++ launcher arguments. For an already
configured tree, its corresponding CMake cache entries must use the absolute profile executable. Ninja and Makefiles
generators are supported. Check provider support separately for workloads using C++ named modules or PCH.

## Interpreting the measurements

- `build_wall_time` is the native build process's elapsed time. It includes cache lookups, Ninja/Make scheduling and linking.
- `ccache` uses an attempt-specific `CCACHE_STATSLOG`. Hit/miss counts and remote read hit/write counts belong to that build
  attempt. When no launcher counters were observed, the report marks them unavailable.
- `sccache` reports deltas of its server counters, including its reported compiler work time. These are labeled
  `shared-server`. Compiles from other clients on that server can contribute to the delta.
- A profile with `externalTelemetry: true` passes an attempt-specific filename in
  `CMAKEWORKFLOW_COMPILER_CACHE_METRICS`. An optional backend observer can atomically publish
  `{"schemaVersion":1,"bytesRead":1024,"bytesWritten":500,"compileSeconds":0.42}` there before the build exits.
  Each metric is optional. Use this for measured transfer bytes and compiler time attributable to the attempt.

The successful node's `compilerCache` object in `run.json` is the source for History. The metric IDs
`compiler_cache_bytes_read`, `compiler_cache_bytes_written` and `compiler_time` are present only when the observer
supplied those measurements. The node also records an opaque cache profile fingerprint for checkpoint recovery.
