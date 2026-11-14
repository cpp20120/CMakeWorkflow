using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Tests.Application;

internal static class FixTests
{
    private static void Check(bool condition, string text)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(text);
        }
    }

    private static async Task Reject(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new System.Diagnostics.UnreachableException("Expected rejection without installation");
    }

    public static async Task RunAsync(string root)
    {
        string lockPath = Path.Combine(root, "fix-locks");
        using FixCoordinator coordinator = new(lockPath);
        using FixCoordinator otherHost = new(lockPath);
        FixRuntime runtime = new();
        FixInstaller installer = new(runtime);
        List<FixProgress> events = [];
        Clock clock = new();

        DoctorSession Session(FixRuntime r, FixInstaller i, CancellationToken disconnected = default) =>
            new(_ => new ToolingService(r, i), _ => Task.CompletedTask, disconnected,
                coordinator, p =>
                {
                    events.Add(p);
                    return Task.CompletedTask;
                }, clock);

        using DoctorSession session = Session(runtime, installer);
        _ = await session.InitializeAsync(new(1, root), default);
        PlanFixRequest request = new(["cmake"], Path.Combine(root, "tools with spaces"), PackageManagerKind.None,
            true);
        FixPlan plan = await session.PlanFixAsync(request, default);
        Check(installer.Calls == 0 && plan.Plan is { Ready: false }, "plan must be read only");
        using DoctorSession stranger = Session(runtime, installer);
        _ = await stranger.InitializeAsync(new(1, root), default);
        await Reject(() => stranger.ApplyFixAsync(new("foreign", plan.PlanId), default));
        Check(installer.Calls == 0, "foreign connection cannot apply a plan");
        FixResult result = await session.ApplyFixAsync(new("success", plan.PlanId), default);
        Check(result.Status == "succeeded" && result.Verification?.Ready == true && installer.Calls == 1,
            "shared installer + authoritative re-probe");
        FixProgress[] progress = [.. events.Where(e => e.OperationId == "success")];
        Check(progress.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, progress.Length))
              && progress[0].Stage == "started" && progress[^1].Stage == "finished"
              && progress.Any(e => e.Stage == "verified_release") && progress.Any(e => e.Stage == "verifying"),
            "fix progress ordering");
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
        FixPlan older = await session.PlanFixAsync(request, default);
        _ = await session.PlanFixAsync(request, default);
        await Reject(() => session.ApplyFixAsync(new("superseded", older.PlanId), default));

        runtime.Installed = false;
        installer.Fail = true;
        plan = await session.PlanFixAsync(request, default);
        result = await session.ApplyFixAsync(new("failure", plan.PlanId), default);
        Check(result.Status == "failed" && result.Verification?.Ready == false
                                        && result.Diagnostics.Any(d => d.Contains("simulated install failure", StringComparison.Ordinal)),
            "install failure is not readiness");
        installer.Fail = false;
        plan = await session.PlanFixAsync(request with { AllowFallback = false }, default);
        int count = installer.Calls;
        result = await session.ApplyFixAsync(new("manual", plan.PlanId), default);
        Check(result.Status == "failed" && installer.Calls == count, "manual tools never silently installed");

        installer.Block = true;
        plan = await session.PlanFixAsync(request, default);
        FixRuntime otherRuntime = new();
        FixInstaller otherInstaller = new(otherRuntime);
        using DoctorSession other = Session(otherRuntime, otherInstaller);
        _ = await other.InitializeAsync(new(1, root), default);
        FixPlan otherPlan = await other.PlanFixAsync(request, default);
        using CancellationTokenSource cancel = new();
        Task<FixResult> running = session.ApplyFixAsync(new("cancel", plan.PlanId), cancel.Token);
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (IDisposable? denied = otherHost.TryAcquire())
        {
            Check(denied is null, "file lock excludes another Host coordinator");
        }

        result = await other.ApplyFixAsync(new("busy", otherPlan.PlanId), default);
        Check(result.Status == "busy" && otherInstaller.Calls == 0, "parallel client cannot install");
        await cancel.CancelAsync();
        result = await running;
        Check(result.Status == "canceled" && result.Verification?.Ready == false, "cancel is explicit with post-check");
        await Reject(() => session.ApplyFixAsync(new("retry", plan.PlanId), default));
        using (IDisposable? available = otherHost.TryAcquire())
        {
            Check(available is not null, "lock released after cancel");
        }

        using CancellationTokenSource disconnected = new();
        installer.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using DoctorSession dropping = Session(runtime, installer, disconnected.Token);
        _ = await dropping.InitializeAsync(new(1, root), default);
        plan = await dropping.PlanFixAsync(request, default);
        running = dropping.ApplyFixAsync(new("disconnect", plan.PlanId), default);
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disconnected.CancelAsync();
        Check((await running).Status == "canceled", "disconnect never implies success");
        await dropping.DrainAsync();
        installer.Block = false;
        installer.Fail = false;
        // A run-bound plan is invalidated by any change to its suspended report.
        string runDir = Path.Combine(root, "runs", "suspended");
        _ = Directory.CreateDirectory(runDir);
        string runFile = Path.Combine(runDir, "run.json");
        const string suspended = /*lang=json,strict*/ """{"status":"needs_attention","findings":[{"suggestedProfiles":["cmake"]}]}""";
        await File.WriteAllTextAsync(runFile, suspended);
        runtime.Installed = false;
        int beforeBoundInstalls = installer.Calls;
        PlanFixRequest bound = request with { RunDirectory = runDir };
        plan = await session.PlanFixAsync(bound, default);
        Check(plan.RunDirectory == runDir && plan.RunSnapshot is not null, "run-bound plan snapshot");
        await File.WriteAllTextAsync(runFile, suspended + " ");
        FixResult stale = await session.ApplyFixAsync(new("stale-run", plan.PlanId), default);
        Check(stale.Status == "run_changed" && installer.Calls == beforeBoundInstalls,
            "stale run prevents installation");
        await File.WriteAllTextAsync(runFile, suspended);
        plan = await session.PlanFixAsync(bound, default);
        using (FileStream activeRun = new(Path.Combine(runDir, ".workflow-run.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            FixResult blocked = await session.ApplyFixAsync(new("bound-busy", plan.PlanId), default);
            Check(blocked.Status == "run_busy" && installer.Calls == beforeBoundInstalls,
                "run lease prevents concurrent installation");
        }

        plan = await session.PlanFixAsync(bound, default);
        result = await session.ApplyFixAsync(new("bound-success", plan.PlanId), default);
        Check(result.ReadyToResume && result.Status == "succeeded", "bound fix rechecked and ready");
        await File.WriteAllTextAsync(runFile, /*lang=json,strict*/ """{"status":"failed","findings":[{"suggestedProfiles":["cmake"]}]}""");
        await Reject(() => session.PlanFixAsync(bound, default));
        await File.WriteAllTextAsync(runFile, suspended);
        await Reject(() => session.PlanFixAsync(bound with { Profiles = ["ninja"] }, default));
        await Reject(() => session.PlanFixAsync(bound with { RunDirectory = root + "-outside" }, default));

        await session.DrainAsync();
        await stranger.DrainAsync();
        await other.DrainAsync();
        // dropping was drained immediately after the disconnect test.
        await VisualStudioFixTests.RunAsync(root);
        Console.WriteLine(
            "Fix checks passed: plans, ownership, expiry, revalidation, installs, failures, locks, cancel and disconnect.");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixRuntime : IToolingRuntime
    {
        public bool Installed;
        public ToolPlatform Platform => ToolPlatform.Linux;
        public bool IsAdministrator => false;

        public string? Resolve(string command, string directory) =>
            command == "c++" || (Installed && command is "cmake" or "ctest") ? Path.Combine(directory, command) : null;

        public Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return !args.SequenceEqual(["--version"])
                ? throw new System.Diagnostics.UnreachableException("Unexpected real install command")
                : Task.FromResult(new ToolCommandResult(0, "version 15.0.0", ""));
        }
    }

    private sealed class FixInstaller(FixRuntime runtime) : ICMakeInstaller
    {
        public int Calls;
        public bool Fail, Block;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InstallAsync(string directory, CancellationToken ct)
        {
            Calls++;
            _ = Started.TrySetResult();
            if (Block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            if (Fail)
            {
                throw new IOException("simulated install failure");
            }

            runtime.Installed = true;
        }
    }
}