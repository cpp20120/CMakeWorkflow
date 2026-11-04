# Stage E2 — SSH.NET transport (opt-in)

This transport is a second client-side implementation of the **same** durable
Host RPC. It uses `SSH.NET` `2026.0.0`, rather than a local `ssh` process. No
Host protocol, workflow scheduler, or remote operation lifetime is changed.

## CLI

```sh
cmakeworkflow remote status \
  --transport sshnet --host build.example --user builder \
  --key-file /home/me/.ssh/id_ed25519 \
  --host-key-sha256 SHA256:YOUR_VERIFIED_HOST_KEY_PIN \
  --endpoint /home/builder/.cmwf/host.sock \
  --workspace /home/builder/project \
  --operation OPERATION_ID
```

Find the host **public key** fingerprint via an independently trusted source
(for example, from the server console with `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`).
Do not blindly copy a fingerprint returned by the same untrusted connection.
The key pin is required and compared in constant time with the SHA-256 hash
of the raw SSH host key; unknown/mismatched keys are rejected.

```csharp
await using var client = await CMakeWorkflowClient.ConnectSshNetAsync(new SshNetHostOptions(
    "build.example", "builder", "/home/me/.ssh/id_ed25519",
    "SHA256:YOUR_VERIFIED_HOST_KEY_PIN", "/home/builder/.cmwf/host.sock", "/home/builder/project"));
var state = await client.Operations.GetAsync(operationId);
```

Use `--transport openssh` (or omit `--transport`) to retain the existing
OpenSSH behavior. The two transports share `WorkflowOperation`, event pages,
reports, artifact catalog and resumable SHA-256 downloads.

## Boundaries

- This SSH.NET slice supports a direct host/port/username and **local private
  key file**. It does not import OpenSSH `~/.ssh/config`, `known_hosts`, proxy
  jumps, ssh-agent, password prompts or passphrase prompts.
- `SshCommand.ExecuteAsync()` runs `host bridge --stdio` as a no-PTY exec
  channel. Its stdin and stdout are raw JSON-RPC byte streams. stderr is
  drained separately with a bounded diagnostic tail.
- Disconnecting kills/disposes only the SSH channel and bridge. An accepted
  workflow belongs to the persistent remote Host, not this connection.
- If the response to `workflow/start` was lost, use `remote find` with the
  **same** request ID. Never repeat a start speculatively.
- SSH.NET is an added dependency in the SDK; its NativeAOT behavior must be
  validated by the existing published consumer smoke in CI.

## Tests

```sh
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll --sshnet-contract-tests
```

A real-sshd smoke is explicitly opt-in. Start a persistent remote Host and
set `CMWF_SSHNET_HOST`, `CMWF_SSHNET_USER`, `CMWF_SSHNET_KEY`,
`CMWF_SSHNET_PIN`, `CMWF_SSHNET_ENDPOINT`, `CMWF_SSHNET_WORKSPACE`, and
optionally `CMWF_SSHNET_PORT`, `CMWF_SSHNET_EXE`. Then run
`--sshnet-live-tests`. This checks a real authenticated binary RPC channel;
no local fake-ssh fixture is treated as equivalent to a real SSH server.
