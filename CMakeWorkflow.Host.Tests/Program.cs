
using CMakeWorkflow.Host;
using CMakeWorkflow.Tooling;

static void SetPrivateUnixDirectory(string path) => SetUnixDirectoryMode(path,
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

static void SetUnixDirectoryMode(string path, UnixFileMode mode)
{
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
}


static void Check(bool condition, string name)
{ if (!condition) throw new Exception(name); }
static async Task Reject<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception(name);
}

var root = Path.Combine(Path.GetTempPath(), "cmwf-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
// Keep Unix socket endpoints short even when the CI workspace/temp path is long.
var socketDirectory = OperatingSystem.IsWindows() ? null
    : Path.Combine("/tmp", "cw-" + Guid.NewGuid().ToString("N")[..12]);
try
{
    if (socketDirectory is not null)
    {
        Directory.CreateDirectory(socketDirectory);
        SetPrivateUnixDirectory(socketDirectory);
    }
    await FixTests.RunAsync(root);
    using var fixes = new FixCoordinator(Path.Combine(root, "locks"));
    var runtime = new FakeRuntime();
    var service = new ToolingService(runtime, new NoInstaller());
    var events = new List<DoctorProgress>();
    var session = new DoctorSession(directory => { Check(directory == root, "workspace passed to runtime factory"); return service; },
        p => { events.Add(p); return Task.CompletedTask; }, default, fixes, _ => Task.CompletedTask);
    var request = new DoctorRequest("test", ["cmake"], Path.Combine(root, "bin with spaces"));
    await Reject<InvalidOperationException>(() => session.DoctorAsync(request, default), "handshake required");
    await Reject<ArgumentException>(() => session.InitializeAsync(new(2, root), default), "protocol mismatch");
    await Reject<ArgumentException>(() => session.InitializeAsync(new(1, "relative"), default), "absolute workspace required");
    var hello = await session.InitializeAsync(new(1, root), default);
    Check(hello.ProtocolVersion == 1 && hello.Capabilities.Contains("tooling/doctor"), "handshake capabilities");
    await Reject<InvalidOperationException>(() => session.InitializeAsync(new(1, Path.GetTempPath()), default), "session cannot change workspace");
    // Provisioning RPC contract: workspace confinement, plan hash and non-mutating apply.
    var environmentFile = Path.Combine(root, "host-environment.json");
    File.WriteAllText(environmentFile, """{"schemaVersion":1,"toolProfiles":["cmake"]}""");
    var environmentPlan = await session.EnvironmentPlanAsync(new(environmentFile), default);
    Check(environmentPlan.Ready && environmentPlan.ApprovalSha256.Length == 64,
        "environment/plan readiness and approval hash");
    await Reject<ArgumentException>(() => session.EnvironmentPlanAsync(
        new(Path.Combine(Path.GetTempPath(), "outside-environment.json")), default),
        "environment plan allowed external requirements");
    await Reject<InvalidOperationException>(() => session.EnvironmentApplyAsync(
        new(environmentFile, new string('0', 64), false), default),
        "environment/apply accepted wrong approval hash");
    var environmentResult = await session.EnvironmentApplyAsync(
        new(environmentFile, environmentPlan.ApprovalSha256, false), default);
    Check(environmentResult.Ready && environmentResult.AppliedSystemChanges?.Count is null or 0,
        "environment/apply no-op recheck");
    Console.WriteLine("Environment Host contract checks passed.");
    var cwd = Directory.GetCurrentDirectory();
    var probesBeforeDoctor = runtime.Calls;
    var plan = await session.DoctorAsync(request, default);
    Check(plan.Ready && runtime.Calls - probesBeforeDoctor == 3, "same tooling service probes");
    Check(events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, 5))
        && events[0].Stage == "started" && events[^1].Stage == "finished", "ordered progress");
    Check(Directory.GetCurrentDirectory() == cwd, "no global cwd mutation");
    runtime.Missing = true;
    Check(!(await session.DoctorAsync(request, default)).Ready, "missing tool is not readiness");
    await Reject<ArgumentException>(() => session.DoctorAsync(request with { InstallDirectory = "relative" }, default), "absolute install directory");
    await Reject<ArgumentException>(() => session.DoctorAsync(request with { Profiles = ["unknown"] }, default), "unknown profile");

    runtime.Missing = false; runtime.Delay = true;
    using var cancel = new CancellationTokenSource();
    var checking = session.DoctorAsync(request, cancel.Token);
    await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancel.Cancel();
    await Reject<OperationCanceledException>(() => checking, "cancel active doctor");
    await session.DrainAsync();
    runtime.Delay = false;
    Check((await session.DoctorAsync(request, default)).Ready, "connection can check again after cancellation");

    using var disconnect = new CancellationTokenSource();
    var disconnectedSession = new DoctorSession(_ => service, _ => Task.CompletedTask, disconnect.Token, fixes, _ => Task.CompletedTask);
    await disconnectedSession.InitializeAsync(new(1, root), default);
    runtime.Delay = true; runtime.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var active = disconnectedSession.DoctorAsync(request, default);
    await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); disconnect.Cancel();
    await Reject<OperationCanceledException>(() => active, "disconnect cancels probe");
    await disconnectedSession.DrainAsync();

    Check(LocalTransport.PipeName(@"\\.\pipe\cmakeworkflow-test") == "cmakeworkflow-test", "Windows pipe name");
    await Reject<ArgumentException>(() => Task.FromResult(LocalTransport.PipeName(@"\\remote\pipe\test")), "remote pipes forbidden");
    if (!OperatingSystem.IsWindows())
    {
        SetPrivateUnixDirectory(socketDirectory!);
        LocalTransport.ValidateUnixEndpoint(Path.Combine(socketDirectory!, "rpc.sock"));
        SetUnixDirectoryMode(socketDirectory!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        await Reject<ArgumentException>(() => Task.Run(() => LocalTransport.ValidateUnixEndpoint(Path.Combine(socketDirectory!, "rpc.sock"))), "public socket directory forbidden");
        SetPrivateUnixDirectory(socketDirectory!);
    }
    var endpoint = OperatingSystem.IsWindows() ? @"\\.\pipe\cmakeworkflow-" + Guid.NewGuid().ToString("N")
        : Path.Combine(socketDirectory!, "rpc.sock");
    using (var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    {
        var server = HostServer.RunAsync(endpoint, lifetime.Token);
        try
        {
            var second = Directory.CreateDirectory(Path.Combine(root, "second workspace")).FullName;
            var codes = await Task.WhenAll(HostCommand.ExecuteAsync(["doctor", "--endpoint", endpoint,
                "--workspace", root, "--profile", "cmake"], lifetime.Token),
                HostCommand.ExecuteAsync(["doctor", "--endpoint", endpoint, "--workspace", second,
                "--profile", "ninja"], lifetime.Token));
            Check(codes.All(code => code is 0 or 2), "real IPC sessions and readiness exit codes");
            var again = await HostCommand.ExecuteAsync(["doctor", "--endpoint", endpoint,
                "--workspace", root, "--profile", "cmake"], lifetime.Token);
            Check(again is 0 or 2, "real IPC reconnect");
            // Exercise the actual RPC framing, generated JSON metadata and method dispatch.
            // The requirements request tools only: no sysctl/cpufreq writes on the test host.
            using (var wire = await LocalTransport.ConnectAsync(endpoint, lifetime.Token))
            using (var rpc = HostConnection.Create(wire))
            {
                var remote = rpc.Attach<IHostRpc>();
                rpc.StartListening();
                var info = await remote.InitializeAsync(new(1, root), lifetime.Token);
                Check(info.Capabilities.Contains("environment/plan") &&
                    info.Capabilities.Contains("environment/apply") &&
                    info.WorkspaceDirectory == root, "wire environment capabilities and workspace");

                var wirePlan = await remote.EnvironmentPlanAsync(new(environmentFile), lifetime.Token);
                Check(wirePlan.Ready && wirePlan.SystemChanges?.Count is null or 0 &&
                    wirePlan.ApprovalSha256.Length == 64, "wire environment/plan response");

                await Reject<StreamJsonRpc.RemoteInvocationException>(
                    () => remote.EnvironmentApplyAsync(
                        new(environmentFile, new string('0', 64), false), lifetime.Token),
                    "wire environment/apply accepted invalid fingerprint");
                await Reject<StreamJsonRpc.RemoteInvocationException>(
                    () => remote.EnvironmentPlanAsync(
                        new(Path.Combine(Path.GetTempPath(), "outside-environment.json")), lifetime.Token),
                    "wire environment/plan accepted path outside workspace");

                var wireResult = await remote.EnvironmentApplyAsync(
                    new(environmentFile, wirePlan.ApprovalSha256, false), lifetime.Token);
                Check(wireResult.Ready && wireResult.AppliedSystemChanges?.Count is null or 0,
                    "wire environment/apply no-op verified");
            }
            Console.WriteLine("Environment JSON-RPC wire checks passed.");
        }
        finally { lifetime.Cancel(); await server; }
    }
    if (!OperatingSystem.IsWindows()) Check(!File.Exists(endpoint), "socket removed after shutdown");
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

sealed class NoInstaller : ICMakeInstaller
{
    public Task InstallAsync(string directory, CancellationToken ct) => throw new Exception("Doctor must never install");
}
sealed class FakeRuntime : IToolingRuntime
{
    public ToolPlatform Platform => ToolPlatform.Linux;
    public bool IsAdministrator => false;
    public bool Missing, Delay;
    public int Calls;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Resolve(string command, string directory) => command is "cmake" or "ctest" or "c++" && !Missing ? Path.Combine(directory, command) : null;
    public async Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        if (!args.SequenceEqual(["--version"])) throw new Exception("Doctor performed mutation");
        Calls++; Started.TrySetResult();
        if (Delay) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new(0, "version 15.0.0", "");
    }
}
