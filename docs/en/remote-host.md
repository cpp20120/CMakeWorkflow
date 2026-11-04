# Remote Host execution

Status: stages A (stdio bridge), B (SSH SDK transport) and C (durable remote
workflow execution) are implemented and validated on the user's Linux host.
Stage C uses the existing typed workflow/operation services over SSH; no new
execution RPC, scheduler or serialization contract is needed. Result downloads,
remote CLI commands and ephemeral activation (stages D–F) remain pending.

## Bridge to a persistent Host

Start a user-scoped Host using the existing local transport. On Linux:

```sh
host_dir=$(mktemp -d /tmp/cmwf.XXXXXX)
cmakeworkflow host serve --endpoint "$host_dir/host.sock"
```

Keep the Host running independently of the bridge. In a separate process:

```sh
cmakeworkflow host bridge --stdio --endpoint /tmp/cmwf.EXAMPLE/host.sock
```

The bridge connects using `LocalTransport`, retaining the private Unix socket
directory and Windows current-user named pipe rules. It forwards binary bytes
unchanged with fixed-size buffers. Stdout is exclusively protocol data;
diagnostics use stderr. EOF or cancellation closes the connection, without
requesting Host shutdown. A bridge exit code is not an acknowledgement that
an RPC request was accepted or executed. Incomplete RPC messages must not be
treated as accepted operations.

Persistent durable operation ownership remains with the existing Host
coordinator. This change adds no scheduler, result store, synchronization or
network listener. Do not interpret local paths as remote paths or automatically
retry uncertain requests.

## SSH SDK connection to an existing Linux Host

Publish the Host using the existing publishing procedure, copy that executable
to the remote machine, and start `host serve` independently as its user. For a
Linux x64 Host the existing publish command is:

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
```

Publishing and machine provisioning are explicit operator actions. This SDK
does not upload executables or start the persistent Host. Configure OpenSSH's
host alias, authentication and known_hosts first; the SDK does not disable
host-key verification, request a password or allocate a terminal.

```csharp
using CMakeWorkflow.Client;

var options = new SshHostOptions(
    "build-linux", "/run/user/1000/cmakeworkflow/host.sock", "/srv/projects/example")
{
    RemoteExecutable = "/home/builduser/bin/CMakeWorkflow",
    // SshExecutable = "ssh", // Override only if needed on the local machine.
    ConnectionTimeout = TimeSpan.FromSeconds(15)
};
await using var client = await CMakeWorkflowClient.ConnectSshAsync(options, cancellationToken);
Console.WriteLine(client.Host.WorkspaceDirectory);
Console.WriteLine(string.Join(", ", client.Host.Capabilities));
// Existing typed services are used unchanged, including read-only operation lookup:
var status = await client.Operations.GetAsync(existingOperationId, cancellationToken);
```

`Endpoint`, `Workspace` and `RemoteExecutable` are remote filesystem values.
Endpoint/workspace must be absolute Linux paths without `.` or `..` components;
the Host validates their existence and existing permission boundaries. No local
absolute-path expansion or source synchronization takes place. The remote
executable may also be a command name resolved by the remote shell's PATH.

The client starts the installed OpenSSH program with `ArgumentList`, `-T` and
`BatchMode=yes`. A POSIX-quoted remote command invokes `host bridge --stdio`.
SSH stdout carries only the existing JSON-RPC framing; stderr is continuously
drained and retained as a bounded 16 Ki-character tail in `SshDiagnostics`.
Connection failures expose this tail and the SSH exit code through
`SshConnectionException`. Protocol and Host admission errors remain distinguishable
from transport failures. No connection or execution request is automatically retried.

The connection timeout bounds the local wait for initialization, independently
of whether the peer acknowledges JSON-RPC cancellation. Timeout throws
`TimeoutException`; caller cancellation throws `OperationCanceledException`.
Both paths terminate/reap the owned SSH process tree before disposing RPC pipes.
All reaping/stderr waits share a separate **two-second cleanup grace** after the
initialization deadline or cancellation, rather than starting independent waits.
A cleanup deadline failure is reported as an I/O error, not as successful cleanup.

Disposal terminates the local SSH process, not the remote persistent Host, and
does not send `operations/cancel`. Existing accepted durable operation ownership
is unchanged. Disposing an SSH client does not have the same semantics as
disposing an SDK-launched private Host. Ephemeral SSH activation is not implemented.

Linux remote targets using a POSIX login shell are the initial target. The SDK
uses cross-platform Process APIs for local clients; Windows local execution,
Windows remote SSH shells and junction behavior have not been tested here.

## Submit an existing remote workflow

The workspace, manifest, source tree, toolchain and required build context must
already exist on the remote machine. The Host reads and snapshots the manifest,
plans/runs CMake, holds build-tree leases and writes results on that machine.
Manifest-relative paths retain their Host-side base directory. The SDK neither
reads these files locally nor uploads or synchronizes them.

Supply the expected SHA-256 of the remote manifest's exact bytes. Obtain it on
the remote machine before submission, for example with
`sha256sum /srv/projects/example/workflow.json`, or retain it from your existing
manifest provisioning process. A stale hash is refused before acceptance.
Both `Manifest` and `ResultsDirectory` are absolute Host paths within the
initialized workspace. Relative paths, paths outside that workspace and symlink
escapes are refused by the Host. Do not call local `Path.GetFullPath` or construct
remote paths using the local platform's path separator.

```csharp
// Save this ID and the exact request inputs in your own local application state
// BEFORE StartAsync. Reload the same ID after a disconnect or client restart.
var requestId = Guid.NewGuid().ToString("N");
var request = new StartWorkflowOptions(
    requestId,
    "/srv/projects/example/workflow.json",
    expectedRemoteManifestSha256,
    Jobs: 2,
    ResultsDirectory: "/srv/projects/example/results");

await using (var submitting = await CMakeWorkflowClient.ConnectSshAsync(options, cancellationToken))
{
    var operation = await submitting.Workflows.StartAsync(request, cancellationToken);
    Console.WriteLine(operation.OperationId); // Persist if received.
} // Disconnect only; the persistent Host still owns the accepted workflow.
```

`StartAsync` sends one durable admission request. Identical, explicitly submitted
requests with the same ID resolve to the same operation, even after completion;
changed inputs with that ID are refused. Acceptance retains an immutable manifest
snapshot. Subsequent edits to the remote manifest do not replace that snapshot.

## Reconnect, inspect and replay

Reconnect to the **same persistent Host and workspace**. Recover by the saved
request ID even if the original acceptance response never reached the client:

```csharp
await using var inspecting = await CMakeWorkflowClient.ConnectSshAsync(options, cancellationToken);
var accepted = await inspecting.Operations.FindAsync(requestId, cancellationToken);
var operation = inspecting.Operations.GetHandle(accepted.OperationId);
var state = await operation.GetSnapshotAsync(cancellationToken);
var recovery = await operation.InspectRecoveryAsync(cancellationToken);
Console.WriteLine($"{state.OperationId}: {state.State}; recovery: {recovery.Decision}");

// Restore the last successfully processed cursor (zero for the first page).
var page = await operation.ReadEventsAsync(savedSequence, limit: 100, ct: cancellationToken);
foreach (var evt in page.Events) Console.WriteLine(evt);
savedSequence = page.NextSequence; // Persist only after processing the page.
// Continue while HasMore; poll later while execution is active.
```

The operation handle is bound to its client connection. After reconnecting, create
a new handle with `Operations.GetHandle`; it performs no submission or resume.
For ordinary live execution `WaitForCompletionAsync(ct: cancellationToken)` polls
until `succeeded`, `failed` or `canceled`. Use bounded caller cancellation, and use
status/recovery inspection for interrupted or uncertain operations.

A transport failure or cancellation of the start RPC does **not** prove that
admission failed. The SDK never reconnects or redispatches automatically. Inspect
using `Operations.FindAsync` first; a missing result is not an instruction to
silently retry with a new request ID. Keep the original inputs and resolve the
uncertainty explicitly. An ambiguous transport result is distinct from the
Host's durable `outcome_unknown` recovery state. Host interruption can require
operator inspection and explicit `ReconcileAsync` with the expected revision
and quiescence confirmation. Event replay is not a recovery checkpoint. No
reconciliation or resume is performed automatically by this connection flow.

To stop an accepted operation, request cancellation explicitly:

```csharp
var current = await operation.InspectRecoveryAsync(cancellationToken);
var receipt = await operation.RequestCancellationAsync(current.Revision, cancellationToken);
Console.WriteLine(receipt.State);
// A cancellation receipt records intent. Inspect/poll until terminal canceled;
// execution may have completed before cancellation. A revision conflict requires
// fresh inspection, not an unconditional overwrite.
```

Closing either the submitting or cancelling SSH session does not revoke durable
acceptance or cancellation intent. These guarantees require the persistent Host
to remain running; termination uses the existing recovery infrastructure.
Results directories in recovery metadata remain **remote** paths. Stage C exposes
state and event replay only; artifact/report downloading belongs to stage D.

## Focused validation

From the repository root, each invocation is bounded:

```sh
timeout 120s dotnet build CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj \
  -c Release --no-restore -m:1 -p:BuildInParallel=false
timeout 30s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --bridge-unit-tests
timeout --kill-after=5s 60s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --bridge-tests
timeout --kill-after=2s 15s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-lifecycle-tests
timeout --kill-after=5s 60s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-tests
timeout --kill-after=5s 60s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-integration-tests
timeout --kill-after=5s 90s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-workflow-tests
```

Unit checks cover binary transfer larger than the copy buffer, client and Host
EOF, cancellation, malformed endpoints and command arguments. The integration
test starts a real Host, connects directly through the existing local SDK,
launches a separate bridge process, performs multiple sequential RPCs, closes
the bridge, then reconnects directly to verify that the Host survived.

The SSH tests use deterministic POSIX process fixtures and the real generated
Host protocol. They check shell quoting, aliases, repeated typed reads, bounded
stderr, invalid paths, missing executables, early SSH exit, malformed/truncated
framing and protocol mismatch. Lifecycle tests assert that a 300ms timeout or
cancellation returns within three seconds including cleanup, and that the owned
process and a descendant retaining stdout/stderr have exited.

The integration fixture substitutes only OpenSSH's network hop with a local
POSIX-shell launcher. It uses the actual bridge and a persistent Host, closes
two SDK connections, and checks direct local reconnection to that same Host.
It needs local IPC, but no sshd, credentials or external network. A real OpenSSH
network connection has not been exercised in this increment.

On 2026-11-04 sandbox integration runs were blocked by `Socket.Bind` returning
`Permission denied`; the user subsequently ran both integration groups on the
Linux host and supplied their passing output. Release builds and socket-free
SSH/lifecycle tests passed in the sandbox. The diagnosed hang was a handshake
waiting for remote acknowledgement after `$/cancelRequest`; local deadline
enforcement and process-first teardown fixed it. Temporary diagnostic tracing
was removed. No full CI matrix or NativeAOT publish/smoke was run for stage B;
the existing source-generated formatter is reused with no new wire DTOs.

Stage C validation on 2026-11-04: the Release Host.Tests build with `--no-restore`
passed with zero warnings/errors. The sandbox again refused `Socket.Bind` before
any scenario ran. The user ran `--ssh-workflow-tests` on the Linux host and supplied
passing output for all four scenarios:

- `execution`: real CMake/Ninja workflow, two clients, disconnect/reconnect,
  request identity/conflict, immutable acceptance snapshot, event cursor replay
  and exactly one external effect.
- `cancellation`: initiating and cancelling client disconnect, revision conflict,
  durable cancellation, process termination and no terminal redispatch.
- `paths`: remote absolute paths, workspace boundaries, symlink escape, missing
  workspace/manifest and stale manifest hash; no rejected request executes.
- `lost-reply`: a test peer drops the successful admission response before the
  SDK sees it. A separate SSH session finds the accepted operation by request ID,
  replays events and observes completion without another start request.

These scenarios use a persistent Host and real bridge subprocesses; only the
lost-reply scenario substitutes a frame-aware fault injector for the bridge.
CMake 3.26+ and Ninja are required. To isolate a failure, append one scenario name
to `--ssh-workflow-tests` rather than rerunning the whole group. The same local
OpenSSH stand-in is used as in B; real sshd/network, Windows and NativeAOT publishing
were not validated by stage C. No production transport, scheduler or RPC behavior
was changed: the existing durable SDK path already provides these operations.

Work stops after stage C. The next stage is remote result retrieval (D).
