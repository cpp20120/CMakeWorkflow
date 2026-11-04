# F3 — real SSH.NET lifecycle acceptance

This suite is **opt-in**, uses an actual Linux `sshd`, and never uses a fake
`ssh` executable. It does not change Host RPCs or production scheduling.

## Prerequisites

Use an **isolated test account/workspace**, not a production checkout. On the
remote machine install the current `CMakeWorkflow` CLI/Host and provide CMake
>=3.26, Ninja, `base64`, `ps`, `grep`, and a POSIX login shell. The workspace
must exist, be absolute, and be writable by the SSH user. For the workflow
scenario it is recommended to run against a disposable VM: the test starts a
real custom CMake target, then deliberately drops the SSH exec channel while
that process is active.

Configure key-based SSH authentication, and verify the host's *public* key
SHA-256 fingerprint via a trusted channel (not the same unauthenticated SSH
connection). These are the same environment variables as `--sshnet-live-tests`
except that a persistent Host or `CMWF_SSHNET_ENDPOINT` is **not required**:

```bash
export CMWF_SSHNET_HOST=build.example
export CMWF_SSHNET_USER=builder
export CMWF_SSHNET_KEY="$HOME/.ssh/id_ed25519"  # absolute local path
export CMWF_SSHNET_PIN='SHA256:TRUSTED_REAL_HOST_KEY_FINGERPRINT'
export CMWF_SSHNET_WORKSPACE=/home/builder/cmwf-f3-workspace
export CMWF_SSHNET_EXE=/home/builder/bin/CMakeWorkflow # optional; defaults to cmakeworkflow
export CMWF_SSHNET_PORT=22                         # optional
```

From the repository root after a Release build:

```bash
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests handshake

dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests workflow

# Optional: only if an independently running persistent Host already exists:
export CMWF_SSHNET_ENDPOINT=/home/builder/.cmwf/host.sock
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests persistent
```

No scenario is skipped silently: missing credentials/settings, prerequisites,
or an SSH server cause a nonzero exit. Omitting the scenario runs handshake and workflow; persistent is separate.

## Checks

- **handshake**: actual SSH.NET authentication and no-PTY exec, private Host
  `initialize` and typed discovery, independent pinned SSH probe confirming
  the remote Host PID exists, channel disposal followed by bounded PID exit;
  a deliberately wrong but syntactically valid host-key pin, a newly generated
  unauthorized private key, and a nonexistent remote binary are all rejected.
- **persistent**: attaches twice to an independently running Host, verifies
  its PID survives both SSH disconnects, and checks typed RPC after reconnect.
  Requires `CMWF_SSHNET_ENDPOINT` and a separately started persistent Host.
- **workflow**: creates only a random `f3-sshnet-*` subdirectory of the
  configured workspace; uploads a compiler-free CMake/Ninja fixture; runs its
  `invoke` workflow; observes `started`; closes the SSH exec channel **without**
  `operations/cancel`; waits for the private Host to exit; releases the gated
  script and checks no `finished` side effect appears. It cleans only its
  generated subdirectory.

The second SSH connection is independent of the SDK channel and pins the same
trusted server key. The probe only runs bounded, fixed test commands.

The suite covers **channel loss caused by disposal**, not a hostile network
partition, host kernel crash, or a server forcibly killing sshd. It does not
prove every process-tree topology. The existing persistent-Host durability,
failed-start and lost-response tests remain separate. No production workflow
is retried after an uncertain admission response.

Real credentials are never committed or provided to GitHub-hosted CI. Run these
checks manually against the VM and record the results before declaring F3
validated. A failed teardown may leave an `f3-sshnet-*` workspace: inspect it
before deleting anything manually.
