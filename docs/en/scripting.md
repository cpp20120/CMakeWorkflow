# Author CMakeWorkflow DAGs in C#

`CMakeWorkflow.Scripting` builds standard schema-v2 workflow manifests from ordinary C#. It depends on
`CMakeWorkflow.Contracts` for authoring and serialization, and on `CMakeWorkflow.Client` for the Host request type.
It works in .NET 10 console and file-based applications, including NativeAOT consumers.

## Run the examples

Pack Contracts, Client and Scripting at a matching version into a local NuGet feed. The examples use `0.1.0-script.1` as a
local development version. For release packages, set the version in the file's `#:package` directive and pass
`-p:ScriptingVersion=VERSION` when running the console project.

Run these commands from the repository root on Linux with .NET 10, CMake 3.26+, Ninja, GCC and Clang.

```sh
feed="$PWD/out/scripting-feed"
for package in Contracts Client Scripting; do
    dotnet pack "CMakeWorkflow.$package/CMakeWorkflow.$package.csproj" -c Release -p:Version=0.1.0-script.1 -o "$feed"
done
export RestoreAdditionalProjectSources="$feed"
dotnet pack examples/scripting/NativeFragments -c Release -p:Version=0.1.0-script.1 -o "$feed"
workspace=$(mktemp -d)
cp examples/scripting/native/* "$workspace/"

# Write the manifest and print its SHA-256. No Host connection is needed.
dotnet run --file examples/scripting/workflow.cs -- "$workspace"

# Write a filtered compiler × configuration benchmark matrix and print its path.
dotnet run --project examples/scripting/Matrix -- "$workspace"
```

The console app demonstrates a reusable [configure/build fragment](../../examples/scripting/NativeFragments/NativeWorkflowFragments.cs)
and a [LINQ matrix](../../examples/scripting/Matrix/Program.cs) with three timing runs per selected variant.
Use the printed matrix manifest path with the existing `CMakeWorkflow workflow run` or Client SDK.
The `process_wall_time` samples for variants such as `gcc-debug` and `clang-release` are available in
the run report and [History](history-compare-example.md).

To run the file-based app through durable Host admission, start a local Host in a private socket directory
in another terminal. Copy the printed endpoint into `endpoint` in the first terminal.

```sh
host_dir=$(mktemp -d)
printf '%s\n' "$host_dir/rpc.sock"
CMakeWorkflow host serve --endpoint "$host_dir/rpc.sock"
```

```sh
# Inspect without dispatch.
dotnet run --file examples/scripting/workflow.cs -- "$workspace" "$endpoint"

# Choose and record a request ID before approving execution.
dotnet run --file examples/scripting/workflow.cs -- "$workspace" "$endpoint" --execute native-smoke-001
```

See [Host IPC](host-ipc.md) for Windows named pipes. The bundled native presets use GCC and Clang available on the Host.

## Script CLI

The [file-based app](../../examples/scripting/native/workflow.cs) consumes the independently built
`CMakeWorkflow.Examples.NativeFragments` package. Copy the full native fixture into a fresh directory and use the local
feed from above. In release-based setups, pack the example library with `-p:ScriptingVersion=VERSION` against your
release SDK, and match the version in the file's package directive to the example library package.

```sh
CMakeWorkflow script compose "$workspace/workflow.cs" -- --matrix
CMakeWorkflow script plan "$workspace/workflow.cs"
CMakeWorkflow script run "$workspace/workflow.cs" --approve --request-id native-job-001
```

`compose` saves the manifest in the C# file's directory by default. Use `--output-dir DIR` to select another existing
controller directory. All modes forward arguments after `--` literally. They run the .NET SDK selected for the C# file's
directory, including its `global.json` and standard package or SDK directives. SDK 10 or later must already be
available on PATH. `--timeout-seconds` controls compilation and authoring, with a default of 300 seconds.

**Run scripts you trust.** Every script command executes arbitrary controller-side C#. Even a compose or plan can write
files, launch programs, access the network and restore NuGet packages. `--approve` authorizes native workflow dispatch
through the Host. The CLI uses an invocation-private file for the response. Version 1 contains
`version`, `relativePaths: "manifest-directory"`, `sha256`, and base64 `manifestBytes`. Standard output and error
carry the script's ordinary messages and .NET diagnostics. The CLI verifies the bytes and discards the response file.

`plan` and `run` use a private local Host by default. Select an existing persistent Host with the connection flags
from [remote CLI](remote-cli.md). For example, if the remote workspace already contains the CMake sources and presets in
`project`, run the locally stored C# file with the following options.

```sh
CMakeWorkflow script run "$workspace/workflow.cs" --approve --request-id native-job-remote-001 \
  --transport sshnet --host builder.example --user build --key-file "$HOME/.ssh/builder" \
  --host-key-sha256 "$known_host_pin" --endpoint /home/build/private/host.sock \
  --workspace /home/build/workspace --manifest-base project --results-dir .cmakeworkflow/remote-job
```

The Host receives only the manifest. Relative paths inside it resolve from the existing Host directory selected with
`--manifest-base`, workspace root by default. This option uses workspace-relative slash-separated components.
`--results-dir` accepts an absolute Host path within the workspace or a workspace-relative path. By default, run
results go under `.cmakeworkflow/script-results/` with a directory name derived from the request ID.
Use `--jobs`, repeatable `--only`, `--history-db` or `--no-history` to set the existing workflow policies.

The CLI prints the request ID before submission. If submission loses its SSH or socket response, it reconnects to the
same Host and looks up that ID. If lookup is unavailable, use `remote find --request-id ID` with the same connection
flags once the Host is reachable. Explicitly retry a request with the same ID and identical parameters. A changed
manifest, result path or execution policy can conflict with an accepted request. On a persistent Host, stopping CLI
observation leaves the durable operation under the Host's control. Use `remote cancel` to request operation cancellation.

## Public API

| API | Role |
| --- | --- |
| `Workflow.Define`, `Context` | Create an authoring scope and specify a build tree, source path, preset, approved variant, or toolchain binding. |
| `Workflow.Scope`, `WorkflowScope.Id` | Qualify node and context identities with explicit dot-separated fragments. Nested scopes share the same workflow. |
| `Inspect`, `Configure`, `Build`, `Invoke`, `CTest` | Declare context-based nodes with explicit IDs and typed prerequisites. |
| `Run`, `Profile`, `ProfileSnapshot` | Declare an executable operation. `Run` and `Profile` require a `TargetBuildRef`; snapshots use an explicit Host path and digest. |
| `Benchmark` | Expand an existing build reference into ordered timing runs with existing benchmark metadata. Each series has a serial dependency chain and requests exclusive execution by default. |
| `Compose(IEnumerable<NodeRef>)` | Capture roots and transitive prerequisites in a `WorkflowManifest`. Use ordinary LINQ and functions to produce the roots. |
| `ToDefinition`, `CopyBytes`, `Sha256` | Read detached DTOs or copies and obtain the digest of the exact serialized bytes. The authoring label is outside the serialized manifest. |
| `SaveAsync`, `AtHost` | Publish locally or point preflight at a manifest already staged on the Host. |
| `WorkflowManifest.PrepareAsync` | Verify and deliver exact bytes using the connected Client. |
| `WorkflowScript.ExportAsync` | Return a manifest from a file-based app through the private response channel. |

Share a reference to one declaration wherever branches share a prerequisite. Compose each independently if
different root selections need different snapshots. The SDK rejects two distinct declarations using the same
ID in the composed graph and rejects references from another scope. `after` preserves first occurrence order.
Root order, dependency order, and iteration order of supplied options form part of the declared input. Materializing
those same inputs again produces the same bytes and SHA-256. Use explicit ordering when inputs originate from
an unordered or external collection.

Use `options:` callbacks on declarations to set existing `WorkflowNode` and `WorkflowContext` fields such as
artifacts, arguments, action cache, resources, provenance, or toolchain selection. The SDK snapshots the
options as soon as the declaration returns. For an artifact input referencing a producer, include the producer
reference in `after` or as the executable source. The existing Host planner validates kind-specific options and
the complete resulting graph. Compose and save only handle authoring data. User C# executes on the controller.

## Admission, remote paths, and recovery

1. Compose and save in a base directory chosen by the caller. Relative paths resolve from the manifest's parent **on the Host**.
2. Call `manifest.PrepareAsync(client.Workflows, resultsDirectory, manifestBase)` for a connected local or remote Host.
   Preparation verifies up to 1 MiB of manifest bytes and atomically publishes the content-addressed filename in an
   existing workspace directory. Existing identical content is reused. An interrupted RPC can be retried with the
   same bytes. On Unix the workspace and selected base must share a filesystem that supports hard links.
   The Host advertises this additive operation as `workflow/prepareManifest`. For older Hosts, stage the file
   explicitly and use `AtHost` with absolute Host paths.
3. Call `client.Workflows.PreflightAsync` to review the typed plan and diagnostics. Optional node selection and
   History policy are on the existing request. Preflight reserves no execution resources.
4. After explicit approval, save a stable caller request ID, then call
   `client.Workflows.StartAsync(preflight.CreateStartOptions(requestId))`. The Host rechecks the bytes at admission.
5. Use `WorkflowOperation` to observe status, read reports, and request durable cancellation. Canceling an RPC or
   polling token stops that client call. If a start response is lost, reconnect to the same workspace and use
   `client.Operations.FindAsync(requestId)`. See [the durable workflow API](workflow-authoring-api.md) for recovery.
