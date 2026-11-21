# Git repository to remote experiment

`experiment plan` resolves a branch, tag or full SHA and saves the commit,
source snapshot, generated workflow and SSH destination on the controller. It
requires Git on a Linux or macOS controller and can fetch a public HTTPS URL or a local
`file:///` Git repository. `plan` prints the workflow and its plan ID.
`run --approve` authorizes this exact plan to execute on the named Host.

Use a disposable or isolated Linux Host with an unprivileged service account,
a dedicated workspace, and limited credentials and network access. CMake
configuration, builds, tests and benchmark programs can execute repository
code with that account's privileges. A source snapshot provides input
integrity. Isolation is the operator's responsibility.

## Run the included CMake project

Install the same CMakeWorkflow build on the controller and Host. Configure
`bench-server` in OpenSSH with its verified host key. On the Host, create the
workspace and start the persistent server as the execution user.

```bash
mkdir -p "$HOME/experiments" "$HOME/.cmwf"
cmakeworkflow host serve --endpoint "$HOME/.cmwf/host.sock"
```

On the controller, from a CMakeWorkflow checkout, publish the example as a
local Git repository. This example assumes the remote user has home directory
`/home/bench`, with CMake 3.26+, Ninja and a C compiler on the Host's PATH.

```bash
example=$(mktemp -d)
cp examples/experiment/* "$example"
git -C "$example" init -b main
git -C "$example" add .
git -C "$example" -c user.name=Example -c user.email=example@example.invalid commit -m "Experiment fixture"

cmakeworkflow experiment plan \
  --repo "file://$example" --ref refs/heads/main \
  --host bench-server --workspace /home/bench/experiments \
  --endpoint /home/bench/.cmwf/host.sock \
  --preset release --ctest --benchmark-target benchmarks --repeats 3

# Use the printed plan ID in both commands.
cmakeworkflow experiment run --plan PLAN_ID --approve
cmakeworkflow experiment report PLAN_ID --wait-seconds 60
```

Use `--repo https://github.com/OWNER/REPO.git` for a public repository.
Fully qualified `refs/heads/NAME` or `refs/tags/NAME` clarify ambiguous
names. Configuring with a preset requires a committed root `CMakePresets.json`.
CMake resolves the preset, with the source and binary directories set to the
accepted snapshot and a fresh operation-owned tree. Use `--config` if the
preset's generator needs a configuration. Set up any project-specific
dependencies on the isolated Host as appropriate.

CTest always runs and requires at least one test. The default build target is
`all` for Ninja or Make. Pass `--build-target TARGET` repeatedly to select
specific targets. `--benchmark-target` must identify a CMake executable.
`--benchmark-arg` passes a literal argument and can be repeated. The report
records process duration for each of up to 200 timing trials. `--profile` adds a separate
CPU capture after the timing trials and requires working Linux `perf` access.
Download the profile artifacts by ID from the result. Analysis and
visualization of raw profiles use the existing profiling tools.

For SSH.NET instead of the OpenSSH alias, pass `--host ADDRESS --user USER
--key-file LOCAL_PATH --host-key-sha256 SHA256:PIN --port 22`. The controller
stores the key file's location. It does not read or transfer the key contents
as part of the plan.

## Use managed tools

For a CMake project that configures directly from its root, select
`--managed-clang --config Release` in place of `--preset release`. This
selects the existing Linux x64 managed Clang, CMake, CTest, Ninja and sysroot
through the Host toolchain binding and configures with Ninja Multi-Config.

Use the Host account to inspect and explicitly provision the toolchain:

```bash
cmakeworkflow host doctor --endpoint "$HOME/.cmwf/host.sock" \
  --workspace "$HOME/experiments" --profile linux-clang --json
cmakeworkflow host fix --endpoint "$HOME/.cmwf/host.sock" \
  --workspace "$HOME/experiments" --profile linux-clang --dry-run --json
cmakeworkflow host fix --endpoint "$HOME/.cmwf/host.sock" \
  --workspace "$HOME/experiments" --profile linux-clang --yes --json
```

Review the dry-run before applying it. Experiments do not run Doctor/Fix
installers or prepare vcpkg packages automatically. See [Host
provisioning](host-ipc.md) for approval and tool policy.

## Report and recovery

The plan ID also identifies the experiment. Moving the branch after planning
has no effect because the approved controller bundle supplies the source
bytes over SSH. Host staging checks both digests and loads the source through
the existing snapshot store. The existing pipeline ledger saves the child
request ID before sending `workflow/start`. If the connection drops or the
start reply is lost, run `experiment report PLAN_ID --wait-seconds 60`. It
recovers by that request ID, then observes the same child operation.
If the Host cannot find the request, the intent remains unresolved. Review
the request ID and Host state before taking any manual action.

The controller stores `result.json`, the SHA-256-verified `run.json` and the
generated `report.html` in
`$XDG_DATA_HOME/CMakeWorkflow/experiments/PLAN_ID` (typically under
`~/.local/share`) or `$CMAKEWORKFLOW_EXPERIMENT_STORE/PLAN_ID` if set.
The result contains repository and source identity, child request and
operation IDs, artifact IDs and hashes, and the full workflow report with
node outcomes, measurements and available build/toolchain provenance. History
uses default Host indexing, which can finish after the operation finalizes.
The saved result records the observed indexing state at retrieval time.

Download a verified log or other artifact named in `result.json`:

```bash
cmakeworkflow experiment download PLAN_ID --artifact stdout:timing_001 --output ./bench.stdout
```

The initial upload supports up to 2,048 regular non-executable files and
8 MiB of source content. Gitlinks/submodules, symlinks, Git LFS pointers,
unsafe paths and checkouts that alter Git blob bytes are rejected. The Host
receives the source and manifest through the `experiment/stage` capability.
This version handles one revision, Host, configuration and optional benchmark
executable per plan.
