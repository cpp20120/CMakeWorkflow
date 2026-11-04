# Stage F2 — explicitly approved SSH.NET Host provisioning

This adapts the **probe → plan → approve → apply → verify** lifecycle used by
`CasinoShiz.Tooling/ToolingService.cs` and the exclusive-deployment concept in
`CasinoShiz.Deploy/Locking/DeploymentLocks.cs`. CMakeWorkflow retains its own
`ToolCatalog` and Doctor/Fix logic; neither Kubernetes nor Ansible is imported.

This is **deployment of an already published Linux Host**, not package-manager
provisioning. It does not use sudo, create remote workspaces, install compilers,
or start/stop a persistent Host. For tools, connect to the Host and use the
existing `tooling/doctor`, `tooling/planFix`, `tooling/applyFix` services.

## Prepare a Linux publish

Build on an environment targeting the **same Linux RID** as the destination.
The publish directory must contain a `CMakeWorkflow` executable and all of its
runtime dependencies:

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o /tmp/cmwf-publish
```

The remote workspace must already exist and be writable by the SSH user.
Host-key SHA-256 pinning and private-key authentication are mandatory. The
host key fingerprint must come from a separately trusted source.

## Read-only plan

```sh
cmakeworkflow remote provision plan \
  --host build.example --user builder \
  --key-file /home/me/.ssh/id_ed25519 \
  --host-key-sha256 SHA256:VERIFIED_PIN \
  --workspace /home/builder/project \
  --publish-dir /tmp/cmwf-publish \
  --install-root /home/builder/.local/share/cmakeworkflow \
  --rid linux-x64 > /tmp/cmwf-f2-plan.json
```

The JSON contains `approvalSha256`, the full ordered file/sha256 inventory,
remote architecture, proposed release directory, and read-only tool probes.
A missing CMake/CTest/Ninja/C++ probe is **reported, not automatically fixed**.
The Host's own Doctor performs detailed tool/version checks after activation.

## Explicit apply

Use the **exact same arguments** plus `--approval-sha256` from the plan.
The apply step recomputes the complete plan before any writes and refuses stale
approval. It installs into a SHA-256-addressed immutable directory and verifies
every uploaded byte on the remote host with `sha256sum -c`, then atomically
switches the `current` symlink. The previous version remains available.

```sh
cmakeworkflow remote provision apply ... --approval-sha256 APPROVAL_DIGEST
```

Use the returned `executable` as `--remote-executable` in `remote` CLI commands,
or `RemoteExecutable` in `SshNetLaunchOptions` / `SshNetHostOptions`.
Existing persistent Host processes are **not** restarted. To run the new binary,
start a new persistent Host explicitly, or launch a private Host through SSH.NET.

### Failure and security boundaries

- SSH.NET pins the host key on **both** exec and SFTP sessions. No SSH agent,
  password authentication or TOFU fallback.
- Destination writes run as the SSH user, with no privileged package changes.
  Linux `sha256sum`, `stat`, `mv`, `ln` and an SFTP subsystem are required.
- Apply takes an exclusive `mkdir` lock. An interrupted installation can leave
  `.bootstrap-lock`; **never steal it automatically**. Inspect any in-progress
  process and manually remove the lock only when no installer is active.
- Temporary stage directories have random names; no existing release content
  is overwritten. The active symlink is replaced atomically via GNU `mv -Tf`.
  This initial implementation targets Linux with GNU coreutils.
- Cancellation is checked at command/file boundaries. A lost SSH connection
  leaves an **uncertain** installation outcome; inspect the remote directory and
  re-plan. Do not assume rollback or issue a blind retry.
- This is not a remote file synchronization API and does not replace durable
  operation admission. It never silently re-submits workflow/start.

## Real-sshd integration

The F3 Docker fixture also runs `--sshnet-provision-live-tests`, covering plan,
wrong approval with zero writes, pinned exec/SFTP transfers, sha256 verification,
private Host launch from the installed version, and repeated idempotent apply.

```sh
bash tests/infrastructure/sshnet/run-tests.sh
```
