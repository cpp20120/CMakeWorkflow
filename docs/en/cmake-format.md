# Formatting CMake and C/C++ sources

```console
cmakeworkflow format --check
cmakeworkflow format --apply
cmakeworkflow format --language cmake --diff
cmakeworkflow format --language cmake --check --file CMakeLists.txt --file "cmake/My Module.cmake" --json
cmakeworkflow format --changed --check
cmakeworkflow format --staged --check
```

By default, the command checks both languages in the current project. Use `--project` to select another project. CMake formatting runs before configure and covers `CMakeLists.txt`, `*.cmake`, and `*.cmake.in`. C/C++ formatting uses `clang-format`. Use `--target` for C/C++ sources from a configured CMake target and `--build-dir` or `--preset` to select its metadata.

## Pinned formatter

```console
cmakeworkflow tooling plan --profile cmakefmt --tool-policy ManagedOnly
cmakeworkflow tooling setup --profile cmakefmt --tool-policy ManagedOnly --yes --plan-sha REVIEWED_SHA256
cmakeworkflow tooling doctor --profile cmakefmt --tool-policy ManagedOnly
cmakeworkflow format --language cmake --check --tool-policy ManagedOnly
```

Use the SHA-256 printed by the plan. Setup verifies the archive and installed tree and publishes the version atomically. Every managed resolution verifies the installed tree again. `AllowSystem` is the default and accepts a compatible `cmakefmt` on PATH before looking in the verified store. The project pins v2.4.0 for Linux x64 (GNU with glibc 2.39 or later, musl otherwise), Linux ARM64 (glibc 2.39 or later), Windows x64 and macOS x64/ARM64. The Linux x64 musl binary is statically linked. Use a system `clang-format` for C/C++. [Upstream v2.4.0 release](https://github.com/cmakefmt/cmakefmt/releases/tag/v2.4.0)

## Selection and results

- Repeat `--file` or use `--include` and `--exclude` with quoted wildcard expressions over project-relative paths with forward slashes. Exclusions win. Use the latter to exclude vendor trees specific to the project. File and directory symlinks are excluded. Known dependency and build output trees are skipped. A source directory called `build` remains eligible unless it contains a CMake cache or an ignore rule excludes it.
- `--changed` selects currently available tracked files changed relative to `HEAD`. `--staged` checks staged paths against the working tree only when Git observes no unstaged changes to those paths. Stage the edits or use `--changed` if the check refuses a staged path. Formatter configuration comes from the working tree. `--staged --apply` is unavailable.
- CMake inputs are intersected with upstream ignore-aware discovery before dispatch. The formatter receives one NUL-delimited list of exact paths, verifies semantics, and uses the configured `--jobs` budget. Upstream reports are checked against that list. Filenames containing CR or LF are rejected because upstream discovery emits one path per line.
- CMake configuration uses the nearest `.cmakefmt.yaml`, `.cmakefmt.yml` or `.cmakefmt.toml`, with `.editorconfig` as upstream fallback. The formatter runs with an empty temporary home. [Upstream configuration reference](https://cmakefmt.dev/config/)
- Exit status `0` means a successful check or apply. `1` means formatting is required in check or diff mode. `2` means a selection, configuration or tool failure. `130` means cancellation. The JSON report records file statuses, tool origins, and upstream CMake JSON. Apply can partially succeed and reports the changes observed on disk.

## Workflow gates

Add a schema-v2 node with `"kind": "format_check"`, a context with `sourceDirectory`, and `"arguments": ["cmake", "managed"]` for a pinned CMake check. The other language choices are `["cpp"]` and `["all"]`. Configure can depend on both checks. The ordinary workflow runner gates dependents on exit status and saves each structured report as the node's stdout artifact. Retrieve it with `WorkflowOperation.ListArtifactsAsync` and `DownloadArtifactAsync`. Client consumers can deserialize it with `FormatReportJson.Default.FormatResult` from Contracts.

C# authoring uses `Workflow.FormatCheck`. Copy the [format gate example](../../examples/scripting/format/) and set the package directive to the installed Scripting package version. Run `script plan workflow.cs` and `script run workflow.cs --approve` using the existing Host setup and approval flow. For SSH, stage the project on the Host and provision formatters on that Host before approving the manifest. Workflow gates check the Host source directory at dispatch. A paused workflow that already passed a gate against live sources requires a new run before further work can use that gate result. Workflow formatting has no Action Cache.

Local .NET callers can use `SourceFormatting.RunAsync(FormatRequest)` or `ClangTools.FormatAsync` with a `FormatTarget`.

The integration runner can also measure formatter batching. Pass the CLI and a native cmakefmt executable.

```console
dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -c Release -- /path/to/CMakeWorkflow --format-benchmark /path/to/cmakefmt --files 1000 --jobs 4 --repeats 5
```
