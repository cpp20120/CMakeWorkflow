using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Host;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Recipes;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Tests;
using CMakeWorkflow.Host.Tests.Application;
using CMakeWorkflow.Host.Tests.Durability;
using CMakeWorkflow.Host.Tests.Execution;
using CMakeWorkflow.Host.Tests.Planning;
using CMakeWorkflow.Host.Tests.Transport;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Tooling.Environment;
using CMakeWorkflow.Tooling.Tools;
using StreamJsonRpc;
using IHostRpc = CMakeWorkflow.Host.Protocol.IHostRpc;

if (args.Length > 0 && args[0] == "--process-recovery-child")
{
    await ProcessRecoveryTests.ChildAsync(args[1..]);
    return;
}
if (args.Length > 0 && args[0] == "--process-recovery-worker")
{
    await ProcessRecoveryTests.WorkerAsync(args[1..]);
    return;
}
if (args.Length > 0 && args[0] == "--process-recovery-tests")
{
    string recoveryRoot = Path.Combine(Path.GetTempPath(), "cmwf-process-recovery-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(recoveryRoot);
    try
    {
        await ProcessRecoveryTests.RunAsync(recoveryRoot);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
        await using TransportTestHost host = new();
        await host.ReadyAsync(deadline.Token);
        await ProcessRecoveryTests.WireAsync(host.Endpoint, host.Root, deadline.Token);
    }
    finally { Directory.Delete(recoveryRoot, true); }
    return;
}

if (args.Length > 0 && args[0] == "--workflow-process-worker")
{
    Environment.ExitCode = await WorkflowProcessTestWorker.RunAsync(args[1..]);
    return;
}

if (args.Length > 0 && args[0] == "--workflow-process-characterization")
{
    string processRoot = Path.Combine(Path.GetTempPath(), "cmwf-process-test-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(processRoot);
    try
    {
        await WorkflowProcessCharacterization.RunAsync(processRoot);
        return;
    }
    finally
    {
        if (Directory.Exists(processRoot))
        {
            Directory.Delete(processRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--workflow-process-backend-tests")
{
    string backendRoot = Path.Combine(Path.GetTempPath(), "cmwf-process-backend-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(backendRoot);
    try
    {
        await WorkflowProcessBackendTests.RunAsync(backendRoot);
        return;
    }
    finally
    {
        if (Directory.Exists(backendRoot))
        {
            Directory.Delete(backendRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--workflow-process-cleanup-tests")
{
    string cleanupRoot = Path.Combine(Path.GetTempPath(), "cmwf-process-cleanup-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(cleanupRoot);
    try
    {
        await WorkflowProcessCleanupTests.RunAsync(cleanupRoot,
            args.Length > 1 ? args[1] : "all");
        return;
    }
    finally
    {
        if (Directory.Exists(cleanupRoot))
        {
            Directory.Delete(cleanupRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--workflow-domain-tests")
{
    string domainRoot = Path.Combine(Path.GetTempPath(), "cmwf-domain-tests-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(domainRoot);
    try
    {
        await WorkflowRunnerDomainCharacterizationTests.RunAsync(domainRoot);
        return;
    }
    finally
    {
        if (Directory.Exists(domainRoot))
        {
            Directory.Delete(domainRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--read-api-wire-tests")
{
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
    await using TransportTestHost host = new();
    await host.ReadyAsync(deadline.Token);
    await ApplicationTests.RunAsync(host.Endpoint, host.Root, deadline.Token);
    return;
}

if (args.Length > 0 && args[0] == "--operation-query-tests")
{
    string queryRoot = Path.Combine(Path.GetTempPath(), "cmwf-operation-query-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(queryRoot);
    try
    {
        OperationListTests.Run(queryRoot);
        HistoryWireModelParityTests.Run();
        HistoryRunsPageTests.Run(queryRoot);
        return;
    }
    finally
    {
        if (Directory.Exists(queryRoot))
        {
            Directory.Delete(queryRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--durable-operation-tests")
{
    string neutralRoot = Path.Combine(Path.GetTempPath(), "cmwf-neutral-operation-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(neutralRoot);
    try
    {
        DurableOperationNeutralTests.Run(neutralRoot);
        return;
    }
    finally
    {
        if (Directory.Exists(neutralRoot))
        {
            Directory.Delete(neutralRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--durable-persistence-tests")
{
    string persistenceRoot = Path.Combine(Path.GetTempPath(), "cmwf-persistence-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(persistenceRoot);
    try
    {
        await DurableByteWriterTests.RunAsync(persistenceRoot);
        await DurablePersistenceTests.RunAsync(persistenceRoot);
        DurableAcceptanceTests.Run(persistenceRoot);
        DurableRecoveryTests.Run(persistenceRoot);
        await ResumeReplayTests.RunAsync(persistenceRoot);
        await DurableExecutionProcessTests.RunAsync(persistenceRoot);
        await DurableCrashTests.RunAsync(persistenceRoot);
        return;
    }
    finally
    {
        if (Directory.Exists(persistenceRoot))
        {
            Directory.Delete(persistenceRoot, recursive: true);
        }
    }
}

if (args.Length > 0 && args[0] == "--execution-engine-tests")
{
    await NeutralExecutionSchedulerTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--workflow-dag-tests")
{
    await WorkflowDagTests.RunAsync();
    WorkflowDagDirectTests.Run();
    return;
}

if (args.Length > 0 && args[0] == "--durable-workflow-wire-tests")
{
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
    await using TransportTestHost host = new();
    await host.ReadyAsync(deadline.Token);
    await DurableWorkflowWireTests.RunAsync(host.Endpoint, host.Root, deadline.Token);
    return;
}

if (args.Length > 0 && args[0] == "--process-domain-wire-tests")
{
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
    await using TransportTestHost host = new();
    await host.ReadyAsync(deadline.Token);
    await ProcessWorkflowWireTests.RunAsync(host.Endpoint, host.Root, deadline.Token);
    return;
}

if (args.Length > 0 && args[0] == "--ssh-workflow-tests")
{
    await SshWorkflowTests.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}

if (args.Length > 0 && args[0] == "--ssh-drop-start-worker")
{
    await SshLostReplyPeer.RunAsync(args[1], args[2]);
    return;
}

if (args.Length > 0 && args[0] == "--ssh-artifact-tests")
{
    await SshArtifactTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--ssh-download-tests")
{
    await SshDownloadTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--artifact-path-tests")
{
    ArtifactPathBoundaryTests.Run();
    return;
}

if (args.Length > 0 && args[0] == "--ssh-lifecycle-tests")
{
    await SshLifecycleTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--ssh-integration-tests")
{
    await SshIntegrationTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--sshnet-contract-tests")
{
    await SshNetContractTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--sshnet-live-tests")
{
    await SshNetContractTests.RunLiveAsync();
    return;
}

if (args.Length > 0 && args[0] == "--sshnet-provision-live-tests")
{
    await SshNetProvisionTests.RunLiveAsync();
    return;
}

if (args.Length > 0 && args[0] == "--sshnet-live-lifecycle-tests")
{
    await SshNetLiveLifecycleTests.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}

if (args.Length > 0 && args[0] == "--ssh-tests")
{
    await SshTransportTests.RunAsync();
    return;
}
if (args.Length > 0 && args[0] == "--ssh-peer")
{
    await SshTestPeer.RunAsync(args[1..]);
    return;
}
if (args.Length > 0 && args[0] == "--ssh-mismatch-peer")
{
    await SshProtocolMismatchPeer.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--bridge-unit-tests")
{
    await BridgeTests.RunUnitAsync();
    return;
}

if (args.Length > 0 && args[0] == "--bridge-tests")
{
    await BridgeTests.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--bridge-worker")
{
    try
    {
        Environment.ExitCode = await HostCommand.ExecuteAsync(["bridge", .. args[1..]], default);
    }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
    catch (Exception error)
    {
        await Console.Error.WriteLineAsync(error.Message);
        Environment.ExitCode = 1;
    }
#pragma warning restore CA1031

    return;
}

static void SetPrivateUnixDirectory(string path) => SetUnixDirectoryMode(path,
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

static void SetUnixDirectoryMode(string path, UnixFileMode mode)
{
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, mode);
    }
}


static void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new System.Diagnostics.UnreachableException(name);
    }
}

static async Task Reject<T>(Func<Task> action, string name) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new System.Diagnostics.UnreachableException(name);
}

if (args.Length > 0 && args[0] == "--durable-fault-worker")
{
    await DurableCrashTests.WorkerAsync(args);
    return;
}

string root = Path.Combine(Path.GetTempPath(), "cmwf-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
// Keep Unix socket endpoints short even when the CI workspace/temp path is long.
string? socketDirectory = OperatingSystem.IsWindows()
    ? null
    : Path.Combine("/tmp", "cw-" + Guid.NewGuid().ToString("N")[..12]);
try
{
    if (socketDirectory is not null)
    {
        _ = Directory.CreateDirectory(socketDirectory);
        SetPrivateUnixDirectory(socketDirectory);
    }

    DurableAcceptanceTests.Run(root);
    BenchmarkRecipeTests.Run(root);
    TypedRecipeTests.Run(root);
    DurableRecoveryTests.Run(root);
    await ResumeReplayTests.RunAsync(root);
    await DurableExecutionProcessTests.RunAsync(root);
    await DurableCrashTests.RunAsync(root);
    await FixTests.RunAsync(root);
    using FixCoordinator fixes = new(Path.Combine(root, "locks"));
    FakeRuntime runtime = new();
    ToolingService service = new(runtime, new NoInstaller());
    List<DoctorProgress> events = [];
    DoctorSession session = new(directory =>
        {
            Check(directory == root, "workspace passed to runtime factory");
            return service;
        },
        p =>
        {
            events.Add(p);
            return Task.CompletedTask;
        }, default, fixes, _ => Task.CompletedTask);
    DoctorRequest request = new("test", ["cmake"], Path.Combine(root, "bin with spaces"));
    await Reject<InvalidOperationException>(() => session.DoctorAsync(request, default), "handshake required");
    await Reject<ArgumentException>(() => session.InitializeAsync(new(2, root), default), "protocol mismatch");
    await Reject<ArgumentException>(() => session.InitializeAsync(new(1, "relative"), default),
        "absolute workspace required");
    HostInfo hello = await session.InitializeAsync(new(1, root), default);
    Check(hello.ProtocolVersion == 1 && hello.Capabilities.Contains("tooling/doctor"), "handshake capabilities");
    Check(hello.Capabilities.Contains("benchmark/prepare"), "Benchmark preparation capability missing");
    string benchmarkOutput = Path.Combine(root, "benchmark definitions", "chain.json");
    BenchmarkPrepareRequest benchmarkRequest = new(Guid.NewGuid().ToString("N"),
        benchmarkOutput, new BenchmarkRecipeV1("benchmark", 1,
            Path.Combine(root, "unconfigured build"), "worker", "Release", 2, 30, ["a b"]));
    BenchmarkPreparation benchmarkPrepared = await session.BenchmarkPrepareAsync(benchmarkRequest, default);
    // Host resolves filesystem aliases (on macOS, /var is often /private/var).
    // Verify the committed bytes through both paths, not lexical path equality.
    byte[] preparedBytes = await File.ReadAllBytesAsync(benchmarkPrepared.Manifest, CancellationToken.None);
    Check(File.Exists(benchmarkOutput)
          && (await File.ReadAllBytesAsync(benchmarkOutput, CancellationToken.None)).SequenceEqual(preparedBytes),
        "Direct Host Benchmark returned a different manifest than requested");
    Check(benchmarkPrepared.ManifestSha256 == Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(preparedBytes)),
        "Direct Host Benchmark committed manifest hash mismatch");
    Check(await session.BenchmarkPrepareAsync(benchmarkRequest, default) == benchmarkPrepared,
        "Direct Host Benchmark retry is not idempotent");
    await Reject<LocalRpcException>(() => session.BenchmarkPrepareAsync(
            benchmarkRequest with { Recipe = benchmarkRequest.Recipe with { Repeats = 3 } }, default),
        "Benchmark request ID conflict not rejected");
    await Reject<LocalRpcException>(() => session.BenchmarkPrepareAsync(
            benchmarkRequest with
            {
                RequestId = Guid.NewGuid().ToString("N"),
                OutputPath = Path.Combine(Path.GetTempPath(), "outside-benchmark.json")
            }, default),
        "Benchmark output escaped initialized workspace");
    Console.WriteLine("Direct Host Benchmark preparation checks passed.");
    string[] newCapabilities =
    [
        "profileRecipe/prepare", "testMatrix/prepare",
        "buildMatrix/prepare", "benchmarkMatrix/prepare"
    ];
    Check(newCapabilities.All(hello.Capabilities.Contains), "Typed recipe Host capability missing");
    string platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    ProfileRecipeV1 profileRecipe = new("profile", 1,
        [new("main", benchmarkRequest.Recipe.BuildDirectory)], "worker", "Release", 1, 30,
        ["literal argument"], "auto", "cpu", platform);
    string profileOutput = Path.Combine(root, "profile-recipe.json");
    ProfileRecipePrepareRequest profileRecipeRequest = new(Guid.NewGuid().ToString("N"),
        profileOutput, profileRecipe);
    ProfileRecipePreparation profileRecipeReady = await session.ProfileRecipePrepareAsync(profileRecipeRequest, default);
    Check(profileRecipeReady == await session.ProfileRecipePrepareAsync(profileRecipeRequest, default)
          && profileRecipeReady.ManifestSha256 == Convert.ToHexString(
              System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(profileOutput, CancellationToken.None))),
        "Profile recipe Host prepare/hash/replay");
    TestMatrixRecipeV1 testRecipe = new("test-matrix", 1,
        benchmarkRequest.Recipe.BuildDirectory, "worker", "Release",
        [new("unit", LabelRegex: "unit")], "parallel", 30);
    string testOutput = Path.Combine(root, "test-matrix.json");
    TestMatrixPrepareRequest testRequest = new(Guid.NewGuid().ToString("N"), testOutput, testRecipe);
    TestMatrixPreparation testReady = await session.TestMatrixPrepareAsync(testRequest, default);
    Check(testReady == await session.TestMatrixPrepareAsync(testRequest, default)
          && testReady.ManifestSha256 == Convert.ToHexString(
              System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(testOutput, CancellationToken.None))),
        "Test Matrix Host prepare/hash/replay");
    BuildVariantV1[] variants = [new BuildVariantV1("one", benchmarkRequest.Recipe.BuildDirectory, "Release")];
    BuildMatrixRecipeV1 buildRecipe = new("build-matrix", 1, variants, ["worker"]);
    string buildOutput = Path.Combine(root, "build-matrix.json");
    BuildMatrixPrepareRequest buildRequest = new(Guid.NewGuid().ToString("N"), buildOutput, buildRecipe);
    BuildMatrixPreparation buildReady = await session.BuildMatrixPrepareAsync(buildRequest, default);
    Check(buildReady == await session.BuildMatrixPrepareAsync(buildRequest, default)
          && buildReady.ManifestSha256 == Convert.ToHexString(
              System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(buildOutput, CancellationToken.None))),
        "Build Matrix Host prepare/hash/replay");
    BenchmarkMatrixRecipeV1 matrixRecipe = new("benchmark-matrix", 1,
        variants, "worker", 2, 30, ["a b"], [new("case", "--case", ["x", "y"])], "round-robin");
    string matrixOutput = Path.Combine(root, "benchmark-matrix.json");
    BenchmarkMatrixPrepareRequest matrixRequest = new(Guid.NewGuid().ToString("N"),
        matrixOutput, matrixRecipe);
    BenchmarkMatrixPreparation matrixReady = await session.BenchmarkMatrixPrepareAsync(matrixRequest, default);
    Check(matrixReady == await session.BenchmarkMatrixPrepareAsync(matrixRequest, default)
          && matrixReady.ManifestSha256 == Convert.ToHexString(
              System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(matrixOutput, CancellationToken.None))),
        "Benchmark Matrix Host prepare/hash/replay");
    await Reject<LocalRpcException>(() => session.BenchmarkMatrixPrepareAsync(
            matrixRequest with { Recipe = matrixRecipe with { Repeats = 3 } }, default),
        "Benchmark Matrix conflicting request ID was accepted");
    await Reject<LocalRpcException>(() => session.TestMatrixPrepareAsync(
            testRequest with
            {
                RequestId = Guid.NewGuid().ToString("N"),
                OutputPath = Path.Combine(Path.GetTempPath(), "outside-test-matrix.json")
            }, default),
        "Test Matrix output escaped workspace");
    Console.WriteLine("Direct Host typed recipe preparation checks passed.");
    await Reject<InvalidOperationException>(() => session.InitializeAsync(new(1, Path.GetTempPath()), default),
        "session cannot change workspace");
    // Provisioning RPC contract: workspace confinement, plan hash and non-mutating apply.
    string environmentFile = Path.Combine(root, "host-environment.json");
    await File.WriteAllTextAsync(environmentFile, /*lang=json,strict*/ """{"schemaVersion":1,"toolProfiles":["cmake"]}""", CancellationToken.None);
    HostPreparationPlan environmentPlan = await session.EnvironmentPlanAsync(new(environmentFile), default);
    Check(environmentPlan.Ready && environmentPlan.ApprovalSha256.Length == 64,
        "environment/plan readiness and approval hash");
    await Reject<ArgumentException>(() => session.EnvironmentPlanAsync(
            new(Path.Combine(Path.GetTempPath(), "outside-environment.json")), default),
        "environment plan allowed external requirements");
    await Reject<InvalidOperationException>(() => session.EnvironmentApplyAsync(
            new(environmentFile, new string('0', 64), false), default),
        "environment/apply accepted wrong approval hash");
    HostPreparationResult environmentResult = await session.EnvironmentApplyAsync(
        new(environmentFile, environmentPlan.ApprovalSha256, false), default);
    Check(environmentResult.Ready && environmentResult.AppliedSystemChanges?.Count is null or 0,
        "environment/apply no-op recheck");
    Console.WriteLine("Environment Host contract checks passed.");
    string cwd = Directory.GetCurrentDirectory();
    int probesBeforeDoctor = runtime.Calls;
    ToolingPlan plan = await session.DoctorAsync(request, default);
    Check(plan.Ready && runtime.Calls - probesBeforeDoctor == 3, "same tooling service probes");
    Check(events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, 5))
          && events[0].Stage == "started" && events[^1].Stage == "finished", "ordered progress");
    Check(Directory.GetCurrentDirectory() == cwd, "no global cwd mutation");
    runtime.Missing = true;
    Check(!(await session.DoctorAsync(request, default)).Ready, "missing tool is not readiness");
    await Reject<ArgumentException>(() => session.DoctorAsync(request with { InstallDirectory = "relative" }, default),
        "absolute install directory");
    await Reject<ArgumentException>(() => session.DoctorAsync(request with { Profiles = ["unknown"] }, default),
        "unknown profile");

    runtime.Missing = false;
    runtime.Delay = true;
    using CancellationTokenSource cancel = new();
    Task<ToolingPlan> checking = session.DoctorAsync(request, cancel.Token);
    await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cancel.CancelAsync();
    await Reject<OperationCanceledException>(() => checking, "cancel active doctor");
    await session.DrainAsync();
    runtime.Delay = false;
    Check((await session.DoctorAsync(request, default)).Ready, "connection can check again after cancellation");

    using CancellationTokenSource disconnect = new();
    DoctorSession disconnectedSession = new(_ => service, _ => Task.CompletedTask, disconnect.Token, fixes,
        _ => Task.CompletedTask);
    _ = await disconnectedSession.InitializeAsync(new(1, root), default);
    runtime.Delay = true;
    runtime.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<ToolingPlan> active = disconnectedSession.DoctorAsync(request, default);
    await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await disconnect.CancelAsync();
    await Reject<OperationCanceledException>(() => active, "disconnect cancels probe");
    await disconnectedSession.DrainAsync();

    Check(LocalTransport.PipeName(@"\\.\pipe\cmakeworkflow-test") == "cmakeworkflow-test", "Windows pipe name");
    await Reject<ArgumentException>(() => Task.FromResult(LocalTransport.PipeName(@"\\remote\pipe\test")),
        "remote pipes forbidden");
    if (!OperatingSystem.IsWindows())
    {
        SetPrivateUnixDirectory(socketDirectory!);
        LocalTransport.ValidateUnixEndpoint(Path.Combine(socketDirectory!, "rpc.sock"));
        SetUnixDirectoryMode(socketDirectory!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        await Reject<ArgumentException>(
            () => Task.Run(() => LocalTransport.ValidateUnixEndpoint(Path.Combine(socketDirectory!, "rpc.sock"))),
            "public socket directory forbidden");
        SetPrivateUnixDirectory(socketDirectory!);
    }

    string endpoint = OperatingSystem.IsWindows()
        ? @"\\.\pipe\cmakeworkflow-" + Guid.NewGuid().ToString("N")
        : Path.Combine(socketDirectory!, "rpc.sock");
    // This is the budget for the entire sequential Host IPC suite, not for a single
    // operation. Individual cancellation/recovery assertions retain their own short deadlines.
    // A 30-second suite-wide budget could expire while Windows is configuring a later fixture.
    using (CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(3)))
    {
        Task server = HostServer.RunAsync(endpoint, lifetime.Token);
        try
        {
            string second = Directory.CreateDirectory(Path.Combine(root, "second workspace")).FullName;
            int[] codes = await Task.WhenAll(HostCommand.ExecuteAsync([
                    "doctor", "--endpoint", endpoint,
                    "--workspace", root, "--profile", "cmake"
                ], lifetime.Token),
                HostCommand.ExecuteAsync([
                    "doctor", "--endpoint", endpoint, "--workspace", second,
                    "--profile", "ninja"
                ], lifetime.Token));
            Check(codes.All(code => code is 0 or 2), "real IPC sessions and readiness exit codes");
            int again = await HostCommand.ExecuteAsync([
                "doctor", "--endpoint", endpoint,
                "--workspace", root, "--profile", "cmake"
            ], lifetime.Token);
            Check(again is 0 or 2, "real IPC reconnect");
            // Exercise the actual RPC framing, generated JSON metadata and method dispatch.
            // The requirements request tools only: no sysctl/cpufreq writes on the test host.
            using (Stream wire = await LocalTransport.ConnectAsync(endpoint, lifetime.Token))
            using (JsonRpc rpc = HostConnection.Create(wire))
            {
                IHostRpc remote = rpc.Attach<IHostRpc>();
                rpc.StartListening();
                HostInfo info = await remote.InitializeAsync(new(1, root), lifetime.Token);
                Check(info.Capabilities.Contains("environment/plan") &&
                      info.Capabilities.Contains("environment/apply") &&
                      info.WorkspaceDirectory == root, "wire environment capabilities and workspace");

                HostPreparationPlan wirePlan = await remote.EnvironmentPlanAsync(new(environmentFile), lifetime.Token);
                // The RPC must report actual host readiness, not assume that an
                // interactive Windows session has an activated MSVC environment.
                // `cmake` profile includes cxx: an installed but inactive cl.exe
                // correctly makes the plan not-ready, without breaking the API.
                Check(wirePlan.SchemaVersion == 1 &&
                      wirePlan.Tools.Tools.Any(tool => tool.Id == "cxx") &&
                      wirePlan.Ready == (wirePlan.Tools.Ready &&
                                         wirePlan.Findings.All(finding => finding.Status == "ready")) &&
                      (wirePlan.SystemChanges?.Count ?? 0) == 0 &&
                      wirePlan.ApprovalSha256.Length == 64, "wire environment/plan response");

                await Reject<RemoteInvocationException>(
                    () => remote.EnvironmentApplyAsync(
                        new(environmentFile, new string('0', 64), false), lifetime.Token),
                    "wire environment/apply accepted invalid fingerprint");
                await Reject<RemoteInvocationException>(
                    () => remote.EnvironmentPlanAsync(
                        new(Path.Combine(Path.GetTempPath(), "outside-environment.json")), lifetime.Token),
                    "wire environment/plan accepted path outside workspace");

                // Exercise successful approval only when the real host is already
                // ready. A not-ready plan may contain installation steps, which
                // a read-only transport test must not execute on the host.
                if (wirePlan.Ready)
                {
                    HostPreparationResult wireResult = await remote.EnvironmentApplyAsync(
                        new(environmentFile, wirePlan.ApprovalSha256, false), lifetime.Token);
                    Check(wireResult.Ready && (wireResult.AppliedSystemChanges?.Count ?? 0) == 0,
                        "wire environment/apply no-op verified");
                }
                else
                {
                    Check(wirePlan.Tools.Tools.Any(tool => !tool.Installed) ||
                          wirePlan.Findings.Any(finding => finding.Status != "ready"),
                        "not-ready environment plan has no missing requirements");
                    Console.WriteLine("Environment apply no-op skipped: real host requirements are not ready.");
                }
            }

            Console.WriteLine("Environment JSON-RPC wire checks passed.");
            await ToolchainWireTests.RunAsync(endpoint, root, lifetime.Token);
            await ApplicationTests.RunAsync(endpoint, root, lifetime.Token);
            await SdkSmokeTests.RunAsync(endpoint, root, lifetime.Token);
            await ProfileWorkflowWireTests.RunAsync(endpoint, root, lifetime.Token);
            await DurableWorkflowWireTests.RunAsync(endpoint, root, lifetime.Token);
            await DurableResumeExecutionWireTests.RunAsync(endpoint, root, lifetime.Token);
            await DurableCancellationWireTests.RunAsync(endpoint, root, lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            await server;
        }
    }

    if (!OperatingSystem.IsWindows())
    {
        Check(!File.Exists(endpoint), "socket removed after shutdown");
    }

    Console.WriteLine("Host contract, session isolation, progress, cancellation and transport validation passed.");
}
finally
{
    if (socketDirectory is not null && Directory.Exists(socketDirectory))
    {
        SetPrivateUnixDirectory(socketDirectory);
        Directory.Delete(socketDirectory, recursive: true);
    }

    Directory.Delete(root, recursive: true);
}

namespace CMakeWorkflow.Host.Tests
{
    internal sealed class NoInstaller : ICMakeInstaller
    {
        public Task InstallAsync(string directory, CancellationToken ct) =>
            throw new System.Diagnostics.UnreachableException("Doctor must never install");
    }

    internal sealed class FakeRuntime : IToolingRuntime
    {
        public ToolPlatform Platform => ToolPlatform.Linux;
        public bool IsAdministrator => false;
        public bool Missing, Delay;
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Resolve(string command, string directory) => command is "cmake" or "ctest" or "c++" && !Missing
            ? Path.Combine(directory, command)
            : null;

        public async Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout,
            CancellationToken ct)
        {
            if (!args.SequenceEqual(["--version"]))
            {
                throw new System.Diagnostics.UnreachableException("Doctor performed mutation");
            }

            Calls++;
            _ = Started.TrySetResult();
            if (Delay)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return new(0, "version 15.0.0", "");
        }
    }
}