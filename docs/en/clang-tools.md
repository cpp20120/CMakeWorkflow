# Clang formatting and analysis

For the unified formatting command, including CMake and Git file scopes, see [Formatting CMake and C/C++ sources](cmake-format.md).

Run these commands in a CMake source directory or pass `--project DIR`. Install the desired LLVM tools on the controller. Use `--executable PATH` to select a particular installed version.

```sh
cmakeworkflow format --check
cmakeworkflow format --apply
cmakeworkflow format --target runtime --build-dir build
cmakeworkflow lint --target runtime --build-dir build --jobs 8
cmakeworkflow lint --tool clang-check --target runtime --build-dir build
cmakeworkflow analyze --preset clang-release
cmakeworkflow lint --tool iwyu --build-dir build
```

| Command | Tool | Input |
| --- | --- | --- |
| `format` | clang-format | C-family sources and headers, or sources and headers declared for a CMake target |
| `lint` | clang-tidy | Selected translation units and their exact compile commands |
| `lint --tool clang-check` | clang-check | Selected compile commands for syntax and semantic diagnostics |
| `analyze` | Clang Static Analyzer through clang-tidy's `clang-analyzer-*` checks | Selected compile commands |
| `lint --tool iwyu` | Upstream IWYU compilation database driver | Selected compile commands |
| `lint --apply-replacements DIR` | clang-apply-replacements | YAML replacements under DIR |

Formatting defaults to check mode and returns 1 when formatting changes are needed. Only `--apply` edits the selected files. `--batch-size` limits files per invocation, with a second bound on argument length. `--jobs` caps parallel invocations. A target uses CMake's codemodel and includes its declared headers and header file sets. Declare headers in CMake when you want them included in a target-only format. Project-wide formatting skips generated build trees, VCS metadata and common CMake output directories.

For existing builds, select `--build-dir DIR`. The project directory itself and `build` are conventional defaults. Ninja and Makefile generators support `CMAKE_EXPORT_COMPILE_COMMANDS`. When using an existing build with `--target`, request File API metadata before configuration.

```sh
mkdir -p build/.cmake/api/v1/query
touch build/.cmake/api/v1/query/codemodel-v2
cmake -S . -B build -G Ninja -DCMAKE_EXPORT_COMPILE_COMMANDS=ON
```

With `--preset NAME`, CMake evaluates the actual preset and configures a dedicated tree under `.cmakeworkflow/clang/`. This overrides the preset's binary directory for the tooling run. Configuring can execute trusted project CMake code. A pre-existing build with its generated headers is preferable when analysis needs outputs created at build time. For multi-config trees, use `--configuration NAME`.

Analysis filters and shards the compilation database before launching tools. Commands retain the build's original flags and working directories. A shared translation unit uses the selected target or configuration's command when CMake records its object output. Ambiguous entries fail with a diagnostic. Clang-tidy and clang-check run one process per distinct translation unit, with bounded concurrency. The IWYU driver handles its own parallelism with `--jobs`. Install both IWYU and its compatible driver before using it.

Use the project's `.clang-tidy` and `.clang-format` for check and style policy. Override tidy checks with `--checks GLOB`. For an explicit fix workflow, export YAML to an empty directory, review it, then apply it.

```sh
cmakeworkflow lint --target runtime --build-dir build --export-fixes out/tidy-fixes
cmakeworkflow lint --apply-replacements out/tidy-fixes
```

Treat imported replacement YAML as trusted input because it can name files to edit. Tool failures, timeouts and tidy diagnostics promoted to errors give a nonzero exit status. `--timeout-seconds` applies to each tool invocation.
