# Developer Lifecycle

Create a Runtime project, check its prerequisites and build, test, benchmark and package the same Release variant.

```sh
cmakeworkflow new runtime Demo
cd Demo
cmakeworkflow doctor
cmakeworkflow restore
cmakeworkflow build --profile release
cmakeworkflow test --profile release
cmakeworkflow benchmark --profile release --repeats 3
cmakeworkflow pack --profile release
```

Commands discover the nearest parent containing `cmwf.project.json`. Use `--project DIRECTORY` to select an exact root. `new` publishes the SDK lock and the Debug and Release configuration locks from built-in templates. An ambient scaffold uses the local CMake, Ninja and compiler. To bind a managed compiler explicitly, prepare an environment using the [managed tooling commands](project-sdk.md) and pass `--environment-lock SHA256` to `new`.

## Approvals and diagnostics

`inspect --json` shows the evaluated configuration, its setting origins and identities. `doctor` also probes local tooling and verifies an explicitly bound environment lock. Qt and Vulkan dependency diagnostics are unverified until configure on the execution host. For projects with a vcpkg manifest, `restore` reports the existing vcpkg plan and checks its local receipt. The explicit tooling commands provision and install these prerequisites. Attach a prepared vcpkg receipt through an existing workflow configuration if your project requires vcpkg execution binding.

Run `restore --plan` after changing policy or before using a new configuration selection. Apply each approval to the same selection that produced it. Approve the SDK first when the SDK and configuration both changed.

```sh
cmakeworkflow restore --plan
cmakeworkflow restore --sdk-plan-sha SDK_DIGEST
cmakeworkflow restore --matrix --plan
cmakeworkflow restore --matrix --configuration-plan-sha CONFIGURATION_DIGEST
cmakeworkflow build --matrix --plan --json
```

## Operations

| Command | Additional arguments |
| --- | --- |
| `configure` | Configures the selected tree. |
| `build` | `--target NAME` selects a native build target. |
| `test` | `--filter REGEX` and `--label REGEX` select CTest cases. `--target NAME` limits the prerequisite build. |
| `run` | `--target NAME` selects an executable. Tokens after `--` are literal program arguments. |
| `benchmark` | `--target NAME`, `--repeats N` and arguments after `--`. Runtime scaffolds supply a benchmark target and elapsed-time metric. |
| `profile` | `--target NAME`, `--backend auto\|perf\|etw\|instruments` and `--mode MODE`. The Host checks the requested profiler. |
| `pack` | Packages supported, uninstrumented scaffolds with CPack. The package ZIP is tracked as a workflow artifact. |

The execution commands accept `--plan`, `--json`, `--jobs`, `--results-dir`, `--history-db` and `--no-history`. Select a profile or matrix using `--profile` or `--matrix`, narrow by `--variant NAME_OR_ID`, and apply project-relative overrides with `--overrides FILE`. Existing `project sdk`, `project config`, `workflow`, `setup` and `profile run/analyze/replay` commands remain available. Projects without variant configuration use their existing presets or workflows.

Plans are read-only. Execution stores a digest-addressed workflow manifest at the project root. Retain it and its configuration generation for recovery. Each run returns its Host operation ID, results directory, report identity and current History indexing status. Use the existing [workflow report and recovery commands](../../README.md) for investigation. The [JSON envelope schema](../../schemas/developer-result-v1.schema.json) describes the developer CLI output.

## Direct .NET API

Reference the `CMakeWorkflow.Developer` project. `DeveloperLifecycle` supports discovery, creation, SDK and configuration plans, explicit lock publication, inspection, workflow planning and execution.

```csharp
var request = new DeveloperOperationRequest(new ProjectLocation(directory), DeveloperOperationKind.Test);
var plan = await DeveloperLifecycle.PlanOperationAsync(request, ct);
await using var host = await LocalDeveloperHost.OpenAsync(plan.ProjectDirectory, ct);
var execution = await DeveloperLifecycle.ExecuteOperationAsync(
    host.Client, new(request, plan.ManifestSha256, clientRequestId), ct);
var status = await execution.WaitForCompletionAsync(ct: ct);
var report = await execution.GetReportAsync(ct);
```

Persist your client request ID before submission. Use the Client SDK to find an uncertain start by request ID, reconnect to a persistent Host, read events or reports, and request durable cancellation. A cancellation token cancels the current wait or RPC. `WorkflowOperation.RequestCancellationAsync` records a separate Host-side cancellation intent.

For a remote Host, publish the reviewed manifest, stage the source, configuration generation and manifest as a prepared workspace, then use `ExecuteStagedOperationAsync` with explicit Host paths and a connected Client SDK. Toolchain and admission decisions belong to the Host. The independent NativeAOT consumer exercises the direct API without a CLI reference.
