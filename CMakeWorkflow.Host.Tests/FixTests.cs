using CMakeWorkflow.Host;
using CMakeWorkflow.Tooling;

static class FixTests
{
    static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
    static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected rejection without installation");
    }
    public static async Task RunAsync(string root)
    {
        var lockPath = Path.Combine(root, "fix-locks");
        using var coordinator = new FixCoordinator(lockPath);
        using var otherHost = new FixCoordinator(lockPath);
        var runtime = new FixRuntime(); var installer = new FixInstaller(runtime);
        var events = new List<FixProgress>(); var clock = new Clock();
        DoctorSession Session(FixRuntime r, FixInstaller i, CancellationToken disconnected = default) =>
            new(_ => new ToolingService(r, i), _ => Task.CompletedTask, disconnected,
                coordinator, p => { events.Add(p); return Task.CompletedTask; }, clock);
        var session = Session(runtime, installer);
        await session.InitializeAsync(new(1, root), default);
        var request = new PlanFixRequest(["cmake"], Path.Combine(root, "tools with spaces"), PackageManagerKind.None, true);
        var plan = await session.PlanFixAsync(request, default);
        Check(installer.Calls == 0 && !plan.Plan.Ready, "plan must be read only");
        var stranger = Session(runtime, installer); await stranger.InitializeAsync(new(1, root), default);
        await Reject(() => stranger.ApplyFixAsync(new("foreign", plan.PlanId), default));
        Check(installer.Calls == 0, "foreign connection cannot apply a plan");
        var result = await session.ApplyFixAsync(new("success", plan.PlanId), default);
        Check(result.Status == "succeeded" && result.Verification?.Ready == true && installer.Calls == 1,
            "shared installer + authoritative re-probe");
        var progress = events.Where(e => e.OperationId == "success").ToArray();
        Check(progress.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, progress.Length))
            && progress[0].Stage == "started" && progress[^1].Stage == "finished"
            && progress.Any(e => e.Stage == "verified_release") && progress.Any(e => e.Stage == "verifying"), "fix progress ordering");
        await Reject(() => session.ApplyFixAsync(new("replay", plan.PlanId), default));
        Check(installer.Calls == 1, "consumed plan cannot repeat installation");

        runtime.Installed = false;
        plan = await session.PlanFixAsync(request, default);
        runtime.Installed = true;
        result = await session.ApplyFixAsync(new("changed", plan.PlanId), default);
        Check(result.Status == "plan_changed" && result.Installation is null && installer.Calls == 1,
            "changed plan rejected before mutations");
        plan = await session.PlanFixAsync(request, default);
        clock.Now = clock.Now.AddMinutes(11);
        await Reject(() => session.ApplyFixAsync(new("expired", plan.PlanId), default));
        var older = await session.PlanFixAsync(request, default);
        await session.PlanFixAsync(request, default);
        await Reject(() => session.ApplyFixAsync(new("superseded", older.PlanId), default));

        runtime.Installed = false; installer.Fail = true;
        plan = await session.PlanFixAsync(request, default);
        result = await session.ApplyFixAsync(new("failure", plan.PlanId), default);
        Check(result.Status == "failed" && result.Verification?.Ready == false
            && result.Diagnostics.Any(d => d.Contains("simulated install failure")), "install failure is not readiness");
        installer.Fail = false;
        plan = await session.PlanFixAsync(request with { AllowFallback = false }, default);
        var count = installer.Calls;
        result = await session.ApplyFixAsync(new("manual", plan.PlanId), default);
        Check(result.Status == "failed" && installer.Calls == count, "manual tools never silently installed");

        installer.Block = true;
        plan = await session.PlanFixAsync(request, default);
        var otherRuntime = new FixRuntime(); var otherInstaller = new FixInstaller(otherRuntime);
        var other = Session(otherRuntime, otherInstaller); await other.InitializeAsync(new(1, root), default);
        var otherPlan = await other.PlanFixAsync(request, default);
        using var cancel = new CancellationTokenSource();
        var running = session.ApplyFixAsync(new("cancel", plan.PlanId), cancel.Token);
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var denied = otherHost.TryAcquire()) Check(denied is null, "file lock excludes another Host coordinator");
        result = await other.ApplyFixAsync(new("busy", otherPlan.PlanId), default);
        Check(result.Status == "busy" && otherInstaller.Calls == 0, "parallel client cannot install");
        cancel.Cancel(); result = await running;
        Check(result.Status == "canceled" && result.Verification?.Ready == false, "cancel is explicit with post-check");
        await Reject(() => session.ApplyFixAsync(new("retry", plan.PlanId), default));
        using (var available = otherHost.TryAcquire()) Check(available is not null, "lock released after cancel");

        using var disconnected = new CancellationTokenSource();
        installer.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropping = Session(runtime, installer, disconnected.Token);
        await dropping.InitializeAsync(new(1, root), default);
        plan = await dropping.PlanFixAsync(request, default);
        running = dropping.ApplyFixAsync(new("disconnect", plan.PlanId), default);
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); disconnected.Cancel();
        Check((await running).Status == "canceled", "disconnect never implies success");
        await dropping.DrainAsync();
        installer.Block = false; installer.Fail = false;
        // A run-bound plan is invalidated by any change to its suspended report.
        var runDir = Path.Combine(root, "runs", "suspended");
        Directory.CreateDirectory(runDir);
        var runFile = Path.Combine(runDir, "run.json");
        const string suspended = """{"status":"needs_attention","findings":[{"suggestedProfiles":["cmake"]}]}""";
        await File.WriteAllTextAsync(runFile, suspended);
        runtime.Installed = false;
        var beforeBoundInstalls = installer.Calls;
        var bound = request with { RunDirectory = runDir };
        plan = await session.PlanFixAsync(bound, default);
        Check(plan.RunDirectory == runDir && plan.RunSnapshot is not null, "run-bound plan snapshot");
        await File.WriteAllTextAsync(runFile, suspended + " ");
        var stale = await session.ApplyFixAsync(new("stale-run", plan.PlanId), default);
        Check(stale.Status == "run_changed" && installer.Calls == beforeBoundInstalls, "stale run prevents installation");
        await File.WriteAllTextAsync(runFile, suspended);
        plan = await session.PlanFixAsync(bound, default);
        using (var activeRun = new FileStream(Path.Combine(runDir, ".workflow-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await session.ApplyFixAsync(new("bound-busy", plan.PlanId), default);
            Check(blocked.Status == "run_busy" && installer.Calls == beforeBoundInstalls,
                "run lease prevents concurrent installation");
        }
        plan = await session.PlanFixAsync(bound, default);
        result = await session.ApplyFixAsync(new("bound-success", plan.PlanId), default);
        Check(result.ReadyToResume && result.Status == "succeeded", "bound fix rechecked and ready");
        await File.WriteAllTextAsync(runFile, """{"status":"failed","findings":[{"suggestedProfiles":["cmake"]}]}""");
        await Reject(() => session.PlanFixAsync(bound, default));
        await File.WriteAllTextAsync(runFile, suspended);
        await Reject(() => session.PlanFixAsync(bound with { Profiles = ["ninja"] }, default));
        await Reject(() => session.PlanFixAsync(bound with { RunDirectory = root + "-outside" }, default));

        Console.WriteLine("Fix checks passed: plans, ownership, expiry, revalidation, installs, failures, locks, cancel and disconnect.");
    }
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class FixRuntime : IToolingRuntime
    {
        public bool Installed;
        public ToolPlatform Platform => ToolPlatform.Linux;
        public bool IsAdministrator => false;
        public string? Resolve(string command, string directory) => command == "c++" || Installed && command is "cmake" or "ctest" ? Path.Combine(directory, command) : null;
        public Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!args.SequenceEqual(["--version"])) throw new Exception("Unexpected real install command");
            return Task.FromResult(new ToolCommandResult(0, "version 15.0.0", ""));
        }
    }
    sealed class FixInstaller(FixRuntime runtime) : ICMakeInstaller
    {
        public int Calls; public bool Fail, Block;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InstallAsync(string directory, CancellationToken ct)
        {
            Calls++; Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (Fail) throw new IOException("simulated install failure");
            runtime.Installed = true;
        }
    }
}
