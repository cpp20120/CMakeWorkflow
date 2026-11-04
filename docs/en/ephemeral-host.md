# Stage F1 — SSH.NET private Host over stdio

`CMakeWorkflowClient.LaunchSshNetAsync` starts an installed Linux Host in an SSH
exec channel, without a pre-existing daemon or Unix socket. The JSON-RPC
`initialize` handshake is the readiness check. The channel is binary, without
PTY, and diagnostics go only to stderr.

```csharp
await using var client = await CMakeWorkflowClient.LaunchSshNetAsync(
    new SshNetLaunchOptions("builder.example", "builder", "/home/me/.ssh/id_ed25519",
        "SHA256:PIN_FROM_TRUSTED_SOURCE", "/srv/project")
    {
        RemoteExecutable = "/home/builder/bin/CMakeWorkflow"
    }, cancellationToken);

var tools = await client.Toolchains.DiscoverAsync(cancellationToken);
// Keep `client` alive until every operation you started is terminal.
```

The remote executable must already be installed. Private-key authentication and
SHA-256 host-key pinning are mandatory, as with the persistent SSH.NET path.
The private Host shares the existing typed RPC contract and coordinator logic;
no new scheduler, wire messages or result format are introduced.

**Lifetime differs from a persistent Host.** The private Host is owned by its
SSH channel. On a graceful disconnect or client disposal, it cancels its
coordinator, drains active sessions and exits. Unfinished operations are not
expected to survive; a lost SSH connection does not prove an operation did not
start or did not produce external effects. After a crash, inspect the remote
journals explicitly. This mode is for bounded, client-supervised execution,
not fire-and-forget durable submission. SSH channel close is not a guaranteed
remote process kill if a remote process ignores cancellation or the server
becomes unresponsive.

The existing `remote start/find/status` CLI still connects to a persistent Host.
Do **not** replace its connection with `LaunchSshNetAsync` and close it after
`start`: that would cancel the newly accepted work. A future ephemeral CLI
needs a `run --wait` lifecycle and explicit disconnect behavior.

The contract suite exercises the stdio Host handshake and EOF shutdown on all
three CI systems. Real network, sshd authentication, abrupt disconnect and
process-tree cleanup require the separate opt-in live test (Stage F3).
