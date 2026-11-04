# Remote Host execution

Status: stages A (stdio bridge) and B (SSH SDK transport) are implemented.
Stage A and the persistent Host integration for stage B passed on the user's
Linux host. Stages C–F are not part of this increment: remote execution UX,
result downloads, CLI commands and ephemeral activation remain pending.

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

Work stops after stage B. The next stage is durable remote workflow wiring (C).
