# Remote Host execution

Status: stage A (stdio bridge) is implemented, with integration validation
blocked in the current development sandbox. Stages B–F are not implemented.
This is not yet an SSH client or a complete remote execution release.

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
network listener. The typed SSH SDK and remote commands are pending; do not
interpret local paths as remote paths or automatically retry uncertain requests.

## Focused validation and blocker

From the repository root, each invocation is bounded:

```sh
timeout 120s dotnet build CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj \
  -c Release --no-restore -m:1 -p:BuildInParallel=false
timeout 30s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --bridge-unit-tests
timeout --kill-after=5s 60s dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --bridge-tests
```

Unit checks cover binary transfer larger than the copy buffer, client and Host
EOF, cancellation, malformed endpoints and command arguments. The integration
test starts a real Host, connects directly through the existing local SDK,
launches a separate bridge process, performs multiple sequential RPCs, closes
the bridge, then reconnects directly to verify that the Host survived.

On 2026-11-04 the sandbox denied `Socket.Bind` with `SocketException (13):
Permission denied`. The execution policy also rejected an elevated invocation
of the same bounded test. Integration validation must run in an environment
permitting local Unix sockets. Checkpoint A is therefore **not complete**;
do not proceed to stage B until this test passes, including process shutdown.
No complete CI matrix or NativeAOT publication was run for this increment.

Next stages, in order: SSH SDK transport, durable remote workflow wiring,
bounded result retrieval, CLI, then explicitly client-owned ephemeral Host.
