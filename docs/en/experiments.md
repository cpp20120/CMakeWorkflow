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

Review the dry-run before applying it. Experiments prepare vcpkg packages only during the separately approved dependency phase. See [Host
provisioning](host-ipc.md) for approval and tool policy.

## Run an experiment with pinned vcpkg dependencies

On the isolated Linux x86-64 Host, provision the existing managed toolchain
and vcpkg profiles. Run these commands as the account running the Host.

```bash
cmakeworkflow tooling plan --profile linux-clang --profile vcpkg --tool-policy ManagedOnly
cmakeworkflow tooling setup --profile linux-clang --profile vcpkg --tool-policy ManagedOnly --yes --plan-sha TOOL_PLAN_SHA256
cmakeworkflow tooling lock --write
```

The pinned Git revision must track a root `vcpkg.json` and may track a root
`vcpkg-configuration.json`. For the builtin registry use baseline
`84bab45d415d22042bd0b9081aea57f362da3f35`. For a Git registry use a
credential-free HTTPS URL and set both `baseline` and `reference` to the same
full commit. Custom triplets and overlays are unsupported. Both the target and
Host triplets are `x64-linux-cmwf`.

```bash
cmakeworkflow experiment plan --repo REPO_URL --ref COMMIT_SHA \
  --host-pool hosts.json --managed-clang --config Release --vcpkg \
  --benchmark-target benchmarks --repeats 3
cmakeworkflow experiment deps plan PLAN_ID
cmakeworkflow experiment deps apply PLAN_ID --approve --plan-sha DEPENDENCY_PLAN_SHA256
cmakeworkflow experiment run --plan PLAN_ID --approve
cmakeworkflow experiment report PLAN_ID --wait-seconds 60
```

`deps plan` stages the source and prints the exact dependency plan. It binds
the Git commit, Lore snapshot, manifest and configuration bytes, registry
commits, vcpkg executable, triplets and managed environment lock. Review those
inputs before granting permission to execute the registry's port scripts.
Moving the original branch does not change the pinned inputs.

`deps apply` serializes installs of the same digest on the Host. An interrupted
install can be retried with the same approval. A published receipt records the
verified package-tree and chainload toolchain digests and a digest of downloads
retained by vcpkg. vcpkg's resolved package versions and available ABI records
are part of the verified installed tree. If the Host uses binary caching,
configure only trusted cache providers on the Host. A cache hit may have no
corresponding local source downloads. Registry baselines and download evidence
do not make arbitrary port scripts hermetic.

`run --approve` checks the receipt and starts the existing durable operation.
CMake uses the verified installed directory with `VCPKG_MANIFEST_INSTALL=OFF`.
The operation checks the same dependency identity against its materialized
source, including during resume. The final report records the dependency plan,
package-tree and download-tree digests with the managed toolchain identity.
If installation is missing, apply the approved dependency plan first. If the
tree is corrupt, restore it from trusted evidence or use a fresh isolated Host.
Run both SSH cases with `CMWF_SSHNET_TEST_VCPKG=1 bash tests/infrastructure/sshnet/run-tests.sh`.
The [builtin-registry test](../../tests/infrastructure/sshnet/experiment-vcpkg.sh)
consumes cxxopts. The [upstream Git-registry test](../../tests/infrastructure/sshnet/experiment-vcpkg-upstream.sh)
fetches HelloWorld at commit `a0d572ea1d8d62c0154ca82d65397bd4cec195dc`, keeps
its fmt consumer, pins the registry, disables copying an untracked resources
directory and registers the executable with CTest in a derived Git commit.

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

Schema-v2 plans store `plan.json` and `source.zip` together. The plan binds
both the exact archive SHA-256 and the source snapshot identity. The source
limit is 512 MiB total, 128 MiB per file and 16,384 files. Git modes `100644`
and `100755` are supported and executable permissions are included in the
snapshot identity. Gitlinks/submodules, symlinks, Git LFS pointers, unsafe
paths and checkouts that alter Git blob bytes are rejected. Retry `run --plan
PLAN_ID --approve` after an upload interruption to resume in 512 KiB chunks
from the saved Host offset. The controller and Host retain the archive for
recovery. Existing v1 plans continue to use the bounded inline upload.

## Reproduce an upstream C++ project

With managed tools provisioned on an isolated Host, plan and run {fmt} 11.2.0
at its pinned upstream commit. `format-test` is a test executable used as a
repeatable timing smoke workload.

```bash
cmakeworkflow experiment plan --repo https://github.com/fmtlib/fmt.git \
  --ref 40626af88bd7df9a5fb80be7b25ac85b122d6c21 \
  --host bench-server --workspace /home/bench/experiments \
  --endpoint /home/bench/.cmwf/host.sock \
  --managed-clang --config Release --benchmark-target format-test --repeats 3
cmakeworkflow experiment run --plan PLAN_ID --approve
```

The [upstream SSH smoke](../../tests/infrastructure/sshnet/experiment-upstream.sh)
runs this sequence and verifies the CTest log and result. It uses the SSH.NET
fixture variables and an already provisioned managed toolchain.

## Select a Host from a pool

Use a local JSON file listing the Linux Hosts and their existing workspaces.
Each entry has a name and a connection. OpenSSH entries use a configured SSH
alias and its host-key policy.

```json
{
  "hosts": [
    {
      "name": "build-a",
      "connection": {
        "destination": "bench-a",
        "workspace": "/home/bench/experiments",
        "endpoint": "/home/bench/.cmwf/host.sock"
      }
    },
    {
      "name": "build-b",
      "connection": {
        "destination": "bench-b",
        "workspace": "/home/bench/experiments",
        "endpoint": "/home/bench/.cmwf/host.sock"
      }
    }
  ]
}
```

For SSH.NET add `username`, `keyFile` and `hostKeySha256` to the connection.
A relative `keyFile` is resolved beside the pool file. The optional `port` defaults
to 22. The optional `executable` names the Host command and defaults to
`cmakeworkflow`.

```bash
cmakeworkflow experiment plan --repo https://github.com/fmtlib/fmt.git \
  --ref 40626af88bd7df9a5fb80be7b25ac85b122d6c21 \
  --host-pool hosts.json --arch x64 --min-cpus 4 \
  --managed-clang --config Release --benchmark-target format-test --repeats 3
cmakeworkflow experiment run --plan PLAN_ID --approve
```

Planning probes up to 16 entries over SSH. It skips unreachable Hosts, Hosts
without the experiment capabilities and those below the requested CPU count
or on a different architecture. With `--managed-clang` the Host must already
have a usable managed toolchain. With `--preset` CMake still resolves the
preset's compiler when configuring.

The eligible Host with the most processors visible to its .NET process wins.
The order in the pool file breaks ties. This metric reflects Host process
capacity. Resource selection does not measure live load, reserve CPUs or limit
CMake build parallelism.

The plan records the connection and resource evidence. Approval rechecks the
selected Host's architecture, requested minimum CPU count and managed toolchain
identity before staging. If these checks fail, create a new plan to select again.
Recovery follows the approved connection and the saved operation ID.

## Compare and run on multiple Hosts

For revision comparisons, prepare and run both plans with the same repository,
workflow options and Host. `compare` checks verified report bytes and History
comparability evidence before applying a threshold to the median process durations.
It requires at least three timings per revision. The default threshold is 5%.
A regression exits with code 1 and an inconclusive comparison exits with code 2.

```bash
cmakeworkflow experiment compare BASELINE_ID CANDIDATE_ID --threshold-percent 10
```

For platform or compiler comparisons, prepare a plan for each selected Linux
Host with the same snapshot and benchmark target, arguments and repetitions.
The preselected Hosts may use different presets and toolchains. A single command approves the named plans in
order. Each keeps its own request identity and report. Across-Host comparison
prints the recorded platform, compiler and median for each Host.

```bash
cmakeworkflow experiment run --plan FIRST_HOST_ID --plan SECOND_HOST_ID --approve
cmakeworkflow experiment compare FIRST_HOST_ID SECOND_HOST_ID --across-hosts
```

## Use from .NET

On a Linux or macOS controller with Git, `ExperimentPlanner.CreateAsync`
resolves the revision and publishes `plan.json` and `source.zip` together
under `<storeDirectory>/<plan.Id>/`. The planner does not contact the Host
or install tools. Set `ConfigurePreset` to use a committed preset, or set
`Configuration` for managed Clang. The CLI uses the same planner and bundle
format.

```csharp
using CMakeWorkflow.Client.Experiments;

var options = new ExperimentPlanOptions(
    repositoryUrl, revision,
    new ExperimentHost("bench-server", "/home/bench/experiments", "/home/bench/.cmwf/host.sock"))
{
    Configuration = "Release",
    Vcpkg = true,
    BenchmarkTarget = "benchmarks",
    Repeats = 3
};
PreparedExperimentPlan draft = await ExperimentPlanner.CreateAsync(options, storeDirectory, ct);
Console.WriteLine($"{draft.Commit} {draft.SourceSnapshotId}");
Console.WriteLine(draft.PlanPath);
Console.WriteLine(draft.Id);
```

Keep the approved plan ID and reopen the plan before approving execution.
Use its saved Host connection. For vcpkg, first inspect the dependency
plan and obtain separate approval for its exact digest.

```csharp
PreparedExperimentPlan plan = await ExperimentService.ReadPlanAsync(planPath, approvedId, ct);
await using var client = await plan.Host.ConnectAsync(ct);

// Review the dependency plan before calling the overload with the approved digest.
var dependencies = await client.Experiments.PrepareDependenciesAsync(plan, ct: ct);
Console.WriteLine(dependencies.Plan);
// After explicit dependency approval
await client.Experiments.PrepareDependenciesAsync(plan, approvedDependencySha256, ct);

// After explicit execution approval
var operation = await client.Experiments.StartApprovedAsync(plan, ct);
var final = await operation.WaitForCompletionAsync(ct: ct);
var report = await operation.GetReportAsync(ct);
```

For a plan without vcpkg, skip the dependency phase. After a lost admission
reply, use `FindAsync` with the same plan and connection. The SDK uses its
own durable admission ID for each plan. Keep both bundle files together for
later use. Staging verifies the archive against the approved plan.
