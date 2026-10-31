# Local Host IPC preview

> **Language / Язык:** [English](en/host-ipc.md) · [Русский](ru/host-ipc.md) · [Documentation index](README.md)

The Doctor/Fix contract below was introduced on 2026-10-30. The current Host also
exposes workflow operations and history capabilities; negotiate `initialize.capabilities`
and see the [updated English guide](en/host-ipc.md). Installed older binaries may differ.

`CMakeWorkflow host serve` hosts the existing `ToolingService` over local
JSON-RPC. Both the CLI client and the VS Code client can connect to the same
process. Fix delegates installation to the existing ToolingService. Workflow execution,
History and profiling still use their existing CLI entry points. There is no second scheduler, HTTP listener or new
history store.

## Build and use

From this repository:

```sh
dotnet build CMakeWorkflow.slnx -c Release
```

Linux/macOS, first terminal (keep it running):

```sh
host_dir=$(mktemp -d /tmp/cmwf.XXXXXX)
printf 'Host endpoint: %s/rpc.sock\n' "$host_dir"
dotnet bin/Release/net10.0/CMakeWorkflow.dll host serve --endpoint "$host_dir/rpc.sock"
```

Second terminal, substitute the endpoint printed above:

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll host doctor \
  --endpoint /tmp/cmwf.XXXXXX/rpc.sock \
  --workspace "$PWD" --profile cmake --json
```

The directory must exist, be private (0700), and not itself be a symlink. Keep the
socket path within 100 UTF-8 bytes. The server refuses an occupied/stale endpoint;
it never unlinks an existing socket to take ownership. Ctrl+C stops the Host and
removes its socket. After an ungraceful crash, remove the stale socket only after
verifying no Host owns it, or choose a new private directory.

Windows PowerShell, first terminal:

```powershell
dotnet bin/Release/net10.0/CMakeWorkflow.dll host serve --endpoint '\\.\pipe\cmakeworkflow-demo'
```

Second terminal:

```powershell
dotnet bin/Release/net10.0/CMakeWorkflow.dll host doctor `
  --endpoint '\\.\pipe\cmakeworkflow-demo' `
  --workspace "$PWD" --profile cmake --json
```

Named pipes use `PipeOptions.CurrentUserOnly`. An exclusive lock prevents another
Host from joining the same pipe name. No TCP transport is exposed.

Doctor supports repeatable `--profile`, optional absolute or CLI-resolved
`--install-dir`, and `--json`. Exit codes are 0 for all requested tools found,
2 for missing tools, 1 for errors and 130 for cancellation. JSON output is the
existing `ToolingPlan` schema v1; progress and diagnostics go to stderr.

## VS Code

Build/install this checkout of the extension, then configure its CLI executable
or `dotnet` plus DLL prefix arguments to a build containing `host serve`.
Use **CMakeWorkflow → Workflow → … → Tools & Environment → Host Doctor (IPC Preview)**,
or the command palette command **CMakeWorkflow: Host Doctor (IPC Preview)**.

With `cmakeWorkflow.hostEndpoint` empty, the extension lazily launches a private
Host for the selected workspace folder. Repeated checks reuse that process and
open fresh RPC connections. Its endpoint appears in the CMakeWorkflow output;
CLI clients can connect to that endpoint while the Host is running.

Set the resource-scoped `cmakeWorkflow.hostEndpoint` to connect to a manually
started Host instead. The extension never stops an external Host. **Stop Local
Host**, folder removal, configuration changes and extension disposal clean up
extension-owned processes/connections. The Host runs where the workspace
Extension Host runs (SSH, WSL or container). Untrusted workspaces cannot start
Doctor or connect to a Host through this command.

The existing Doctor and Install Tools commands remain available. There is no
automatic probe on extension activation or automatic installation. Probe readiness
means presence and version, not compiler feature support or profiling permissions.
Host Doctor offers **Fix Missing Tools** when tools are missing. You can also use
**CMakeWorkflow: Fix Tools (Host)** in the same menu. Both paths show a fresh plan
and require **Fix Tools** confirmation before applying it.

After successful installation and verification, the extension saves
`cmakeWorkflow.toolingInstallDirectory` for that workspace folder and prepends it
to PATH for subsequent CLI/owned-Host child processes. Global PATH and shell files
are untouched. Existing CMake trees retain their recorded CMake/compiler identity;
Fix does not silently migrate their toolchains. An external Host retains its own
startup environment; Doctor/Fix receive the selected install directory explicitly.

## Protocol v1

Transport: StreamJsonRpc 2.26.150 with `HeaderDelimitedMessageHandler` and
`SystemTextJsonFormatter`; TypeScript uses vscode-jsonrpc 10.0.0. UTF-8 JSON-RPC
2.0 frames have `Content-Length` headers. Arguments are positional, e.g.:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":[{"protocolVersion":1,"workspaceDirectory":"/absolute/workspace"}]}
```

| Method | Request | Response |
|---|---|---|
| `initialize` | `protocolVersion`, existing absolute `workspaceDirectory` | protocol version, process ID, workspace, capabilities, supported tooling profiles |
| `tooling/doctor` | `operationId`, `profiles` array, nullable absolute `installDirectory` | existing `ToolingPlan` v1 |
| `tooling/progress` notification | `operationId`, consecutive `sequence` starting at 1, `stage`, nullable `tool` | none |

Progress stages are `started`, `tool_checked`, `finished`. A `tool_checked` event
contains the existing `ToolStatus` DTO. No successful result is accepted by the
TypeScript client without final progress. `$/cancelRequest` is supported by the
RPC libraries. The TypeScript client additionally closes its connection on
cancellation/timeout; disconnect cancels active probes. The Host drains probe
cleanup before disposing its session. Doctor operations are not durable:
reconnect starts a new explicit Doctor check; no operation is automatically
resumed or retried.

The handshake must precede tooling requests. A connection cannot change its workspace.
The Host never mutates process-wide current directory or environment. At most
16 clients are active, with one probe at a time per connection. Progress is
scoped to the requesting connection and operation ID. This is local same-user
IPC, not a sandbox for hostile clients or repository-controlled executables.

## NativeAOT and tests

The public RPC interface uses source-generated PolyType target metadata and
StreamJsonRpc proxies. JSON DTOs and RPC error data have `JsonSerializerContext`
metadata. Formatter options explicitly match that context. The protocol avoids
RPC marshalable objects and generic RPC streams/progress objects.

```sh
dotnet run --project CMakeWorkflow.Host.Tests
dotnet run --project CMakeWorkflow.Tooling.Tests
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 \
  -o out/host-native -p:TreatWarningsAsErrors=true
```

From the extension repository, set the path to that actual native executable:

```sh
CMAKEWORKFLOW_HOST_TEST_BINARY=/absolute/path/to/CMakeWorkflow npm test
npm run package
```

Without that variable the real CLI/Host integration test is explicitly skipped;
mock IPC, protocol and UI lifecycle tests still run. The integration test covers
C# and TypeScript clients, multiple workspaces, incompatible handshake, unknown
profile, reconnect, endpoint collision and cleanup. On Unix it also cancels an
actual slow probe and checks that its PID exits. It never installs packages.
Fix unit tests use fake installers/runtimes for success, failure, stale/expired/foreign
plans, repeated application, concurrency, cancellation and disconnect. Native IPC
Fix tests use `--pm none --no-fallback`, so they cannot install packages or download
releases. The runner CI includes Host session/IPC checks on its existing OS matrix.
Windows/macOS runtime and packaging must be verified on those platforms; Linux
results do not establish that they pass.

## Explicit Fix contract

Read-only preview (no installation):

```sh
CMakeWorkflow host fix --endpoint ENDPOINT --workspace "$PWD" --profile native --dry-run --json
```

Explicitly authorize the current plan:

```sh
CMakeWorkflow host fix --endpoint ENDPOINT --workspace "$PWD" --profile native --yes --json
```

Without `--yes`, Fix prints a plan only (exit 2 when tools are missing; dry-run
exits 0). Optional `--pm auto|none|apt|pacman|dnf|brew|winget`, `--no-fallback`
and `--install-dir DIR` use existing provisioning policies. `--pm none --no-fallback`
permits verification/manual diagnostics only. With `--yes`, the plan is also
printed to stderr; stdout with `--json` contains only the final FixResult.
The CLI prints environment guidance but does not modify your parent shell's PATH.

Additional negotiated capabilities (protocol remains v1):

| Method | Request | Response |
|---|---|---|
| `tooling/planFix` | `profiles`, nullable absolute `installDirectory`, `packageManager` enum string, `allowFallback` boolean | `schemaVersion:1`, `planId`, `expiresAt`, `plan` |
| `tooling/applyFix` | `operationId`, `planId` | `schemaVersion:1`, `status`, nullable `installation`, nullable `verification`, `diagnostics` |
| `tooling/fixProgress` notification | `operationId`, ordered `sequence`, `stage`, nullable `tool`, nullable `message` | none |

Progress stages: started, package_index, package_install, verified_release,
tool_ready, tool_failed, verifying, finished. ToolingResult and ToolingPlan are
reused for installation and verification. Result status is succeeded, failed,
canceled, plan_changed or busy. Success requires both successful installation
and a ready post-install plan. Expired, foreign and consumed IDs are RPC errors.

Plans are connection-bound, single-use, expire after ten minutes, and each new
plan replaces the previous one on that connection. Host retains the plan; clients
cannot supply installation commands. Immediately before execution Host re-probes
and compares the complete logical plan under the installation lock. A changed plan
is rejected before any installation. A CLI preview followed by a separate CLI
`--yes` invocation creates a new plan: `--yes` authorizes that current plan, not a
saved preview token. Package versions and release artifacts are not pinned by this
logical-plan mechanism; the package manager/release source still selects them.

A per-user file lock serializes Fix across Host processes, plus an in-process gate.
Native package-manager locks remain authoritative. Older direct `tooling setup`
commands do not participate in the Host lock. Busy attempts do not queue or retry;
review a new plan later.

Linux package installation uses `sudo -n`; Host does not collect passwords or run
permanently as root. If authorization is unavailable, diagnostics tell you to
install the reviewed packages in a terminal and check again. Manual-only tools
remain explicit failures. Automatic elevation/UAC prompting is not added here.

**Cancellation/disconnect is interruption, not rollback.** Active installer
processes use the existing runtime's cancellation behavior. Host attempts a bounded
post-check even after a failure or cancellation, but a disconnected client or a
forcibly stopped Host may not receive/save a result. Closing VS Code, removing a
folder or changing Host configuration can interrupt its owned Host. Keep it open
during Fix. If the result is lost, run Doctor before attempting another Fix; never
infer that installation succeeded or automatically retry it. This slice does not
provide durable installation operations, recovery journals or resume-after-crash.
