# Real-sshd SSH.NET acceptance (F3)

This **test-only** Docker fixture exercises an actual TCP/OpenSSH server using
SSH.NET in `CMakeWorkflow.Host.Tests`. It needs Linux, Docker, `dotnet` SDK 10,
`ssh-keygen`, `ssh-keyscan`, and `timeout`:

```bash
bash tests/infrastructure/sshnet/run-tests.sh
```

The harness publishes a **framework-dependent** Linux CLI, builds Host.Tests,
generates a fresh client ED25519 key, creates a disposable Ubuntu 24.04-based
container running sshd with public-key-only authentication, and runs the live
`handshake`, `workflow` and `persistent` scenarios. It mounts just the CLI
publish directory read-only and ephemeral keys (read-only). The SSH port binds
only to `127.0.0.1` on a dynamically assigned port. The host-key SHA-256 pin is
read from the container using Docker control, not trusted from SSH.

The ephemeral Host must stop on SSH channel teardown, including an active
CMake/Ninja workflow. The separately started persistent Host must survive
multiple SSH.NET bridge connections. Docker teardown removes the container and
keys; the workflow test owns only a random child of the unprivileged workspace.
No passwords, SSH agents, external hosts, production workspaces or persistent
credentials are used.

This exercises **Linux client → Linux Host**, not Windows/macOS SSH clients,
remote WAN failures, machine provisioning or NativeAOT-in-container. The
existing CI matrix separately checks NativeAOT. The independent Actions
workflow `.github/workflows/sshnet-real-sshd.yml` runs this fixture but does
**not** gate the current tag-publishing job; make its status a required check or
add an explicit release dependency before relying on it as a release gate.

The F3 test implementation is included in the same combined patch so this
fixture can be applied to `feature/remote-host` before the earlier F3 patch.
