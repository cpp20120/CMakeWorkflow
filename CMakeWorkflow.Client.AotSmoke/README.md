# NativeAOT SDK consumer smoke

This test connects to an **already running Host** and an **existing workspace**.
It checks actual source-generated RPC serialization with JSON reflection disabled:

- `initialize` and workspace binding;
- `workflow/plan` for its own generated inspect-only manifest;
- typed `workflow/start`, `operations/get`, `operations/find`, including duplicate request identity;
- typed `operations/events` and `workflow/recoveryInspect`;
- rejected `operations/get`: error code `1001`, structured `ApplicationError`
  deserialized as SDK `RpcErrorData` (`not_found` and a nonempty message), followed
  by a successful call on the same connection;
- a pre-canceled request token and a terminal operation's cancellation no-op;
- active `operations/cancel`: typed receipt, persisted canceled outcome, and
  retry of the original start request without dispatching the effect again;
- reconnect after disposal without stopping the externally owned Host.

The first fixture has one `inspect` node. A separate cancellation fixture uses
CMake 3.26+ and Ninja from PATH to configure a compiler-free `project(... NONE)`
and invoke a generated target that writes a start marker, waits 30 seconds, and
writes a finish marker. The smoke cancels it after observing the start marker and
checks that only one start and no finish were recorded. No repository workflows
or installers are executed. Fixture/result files and operation metadata are
written under the supplied workspace. Use an isolated temporary workspace.

From the repository root, publish the SDK as a **local NuGet package** and consume
that package instead of the normal development ProjectReference:

```sh
dotnet pack CMakeWorkflow.Client/CMakeWorkflow.Client.csproj -c Release \
  -p:PackageVersion=0.0.0-smoke.1 -o out/sdk-packages
dotnet publish CMakeWorkflow.Client.AotSmoke/CMakeWorkflow.Client.AotSmoke.csproj \
  -c Release -r linux-x64 -p:SdkPackageVersion=0.0.0-smoke.1 \
  -p:RestoreAdditionalProjectSources="$PWD/out/sdk-packages" \
  -p:TreatWarningsAsErrors=true --artifacts-path out/sdk-smoke-artifacts -o out/sdk-aot
out/sdk-aot/CMakeWorkflow.Client.AotSmoke --verify-package \
  out/sdk-packages/CMakeWorkflow.Client.0.0.0-smoke.1.nupkg \
  out/sdk-smoke-artifacts/obj/CMakeWorkflow.Client.AotSmoke/project.assets.json \
  0.0.0-smoke.1
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 \
  -p:TreatWarningsAsErrors=true -o out/native
```

Choose a fresh local package version when repacking changed SDK sources, to avoid
reusing NuGet's cached package. No package is uploaded to a registry.

The package audit checks a strict asset allowlist (SDK DLL and NuGet metadata),
the declared StreamJsonRpc dependency, and every compile/runtime target in the
consumer's `project.assets.json`. It rejects source-project dependencies and
compares the actual archive's SHA512 with NuGet's restored package hash. The
package-mode build also rejects any `ProjectReference`. SDK dependency upgrades
or intentional new package assets require updating the audit. Transitive runtime
dependencies are resolved by NuGet, not bundled as Host/engine assemblies.

Example Unix run (short private socket directory, spaces in workspace path):

```sh
smoke_dir=$(mktemp -d /tmp/cmw-sdk.XXXXXX)
mkdir "$smoke_dir/workspace with spaces"
out/native/CMakeWorkflow host serve --endpoint "$smoke_dir/rpc.sock" \
  >"$smoke_dir/host.stdout" 2>"$smoke_dir/host.stderr" &
host_pid=$!
trap 'kill "$host_pid" 2>/dev/null || true; wait "$host_pid" 2>/dev/null || true; rm -rf "$smoke_dir"' EXIT
for attempt in {1..150}; do
  test -S "$smoke_dir/rpc.sock" && break
  kill -0 "$host_pid" 2>/dev/null || { cat "$smoke_dir/host.stderr"; exit 1; }
  sleep 0.1
done
test -S "$smoke_dir/rpc.sock"
out/sdk-aot/CMakeWorkflow.Client.AotSmoke "$smoke_dir/rpc.sock" "$smoke_dir/workspace with spaces"
```

The consumer times out after 90 seconds and exits nonzero on any mismatch. A
successful publish alone is not a passing smoke: the native executable must emit
the final `PASS NativeAOT SDK smoke` line and exit 0. Windows uses a local
`\\.\pipe\cmakeworkflow-...` endpoint and `.exe` executables; select its RID when
publishing. Platform support must be validated on that platform.

Request-token cancellation and durable `operations/cancel` are distinct: canceling
a client wait does not revoke accepted work. The default smoke exercises a
pre-canceled token and explicit durable cancellation. The separate in-flight
mode below checks actual JSON-RPC `$/cancelRequest` delivery. A single stopped
fixture is not a general process-tree cleanup guarantee for arbitrary programs.

## In-flight RPC cancellation

Publish an isolated native test Host and use the same package-based native
consumer. Keep the test Host's build intermediates and output separate from the
production publish:

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 \
  -p:RpcCancellationTesting=true -p:TreatWarningsAsErrors=true \
  --artifacts-path out/rpc-test-artifacts -o out/rpc-test-native
```

```powershell
./scripts/ci-native-smoke.ps1 -Cli out/rpc-test-native/CMakeWorkflow -Consumer out/sdk-aot/CMakeWorkflow.Client.AotSmoke -InFlight -LogDirectory out/smoke-logs/inflight
```

Use the platform's RID and `.exe` suffix on Windows. The script sets
`CMAKEWORKFLOW_RPC_TEST_ROOT` only in its child Host, inside the isolated existing
workspace, and passes `--inflight` to the consumer. The consumer sends a real
`workflow/plan` request through the SDK's generated proxy. The test Host pauses
inside the actual handler/session gate and writes a `started` marker. Only then
does the consumer cancel its token. Success requires:

- `OperationCanceledException` from the pending client request;
- server acknowledgement that the RPC token was canceled while the connection
  remained open, plus a marker that the paused handler exited;
- a second gated request, explicitly released without cancellation, returning a
  valid workflow plan on the **same connection**.

Timeouts and disconnects cannot satisfy the server acknowledgement. A missing
test gate fails the test rather than skipping it. Polling observes markers; it
does not decide when to cancel based on elapsed time. The script retains markers
under the smoke log directory before cleaning up its temporary workspace.

The hook is compiled only with `RpcCancellationTesting=true`; production Host
binaries have neither the gate nor its environment-variable behavior. It adds
no RPC methods, SDK dependencies or public wire fields. Test Host binaries are
not included in downloadable CI builds. This covers cancellation delivery and
handler/session cleanup for a read RPC; it does not establish interruption
guarantees for every filesystem, SQLite or external process operation, nor does
it revoke an already accepted `workflow/start` mutation.

## GitHub Actions builds

The [CI workflow](../.github/workflows/ci.yml) runs on pushes, pull requests and
manual **Run workflow** requests. Each platform runs all six test executables
(Campaigns, Integration, History, Profiling, Tooling and Host), the isolated
recovery fault-injection suite, and the native CLI/Host/SDK smoke tests (including
the isolated in-flight cancellation test):

| Runner | Native RID | Host transport |
| --- | --- | --- |
| Ubuntu 24.04 | `linux-x64` | Unix socket |
| Windows | `win-x64` | Named pipe |
| macOS 26 (Apple Silicon) | `osx-arm64` | Unix socket |

After a platform's checks pass, open **Actions → CI → run → Artifacts** and download
`CMakeWorkflow-<rid>-<run-id>-<attempt>`. Each artifact contains:

- `CMakeWorkflow-<rid>.tar.gz`: complete native CLI/Host publish output;
- `CMakeWorkflow.Client.AotSmoke-<rid>.tar.gz`: complete native consumer output;
- `CMakeWorkflow.Client.0.0.0-smoke.<run-id>.<attempt>.nupkg`: the audited SDK package;
- `build.json` with the commit, RID and run identity, and `SHA256SUMS`.

Extract the inner archives with `tar -xzf FILE.tar.gz` into separate directories.
The tar archives preserve Unix executable permissions and include native runtime
dependencies (such as SQLite) and available debugging symbols. CLI/Host binaries
are platform-specific; the SDK itself targets `net10.0` and is platform-neutral.

Build artifacts are retained for 30 days. A separate `smoke-logs-<rid>-...`
artifact retains available package audit, consumer and Host logs for 14 days,
including failed smoke runs. Full test output remains in the Actions step logs.
An artifact requires that platform's job to pass, not the other matrix jobs.
These are downloadable CI builds; this workflow does not push packages to
nuget.org or create GitHub Releases. No publishing credentials are needed.

The shared smoke runner is `scripts/ci-native-smoke.ps1` (PowerShell 7). It waits
for the platform's IPC endpoint, runs against an existing temporary workspace
with spaces, and stops its own Host in `finally`. To repeat locally after publish:

```powershell
./scripts/ci-native-smoke.ps1 -Cli out/native/CMakeWorkflow -Consumer out/sdk-aot/CMakeWorkflow.Client.AotSmoke
# Windows: append .exe to both executable paths.
```
