using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.VisualStudio;

namespace CMakeWorkflow.Host.Tests.Application;

internal static class VisualStudioFixTests
{
    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(description);
        }
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new System.Diagnostics.UnreachableException("Expected rejected Visual Studio fix.");
    }

    public static async Task RunAsync(string root)
    {
        using FixCoordinator coordinator = new(Path.Combine(root, "vs-fix-locks"));
        FakeBackend backend = new();
        List<FixProgress> progress = [];
        DoctorSession Session() => new(
            _ => throw new System.Diagnostics.UnreachableException("Ordinary tooling must not run"),
            _ => Task.CompletedTask, default, coordinator,
            item => { progress.Add(item); return Task.CompletedTask; },
            createVisualStudio: () => new VisualStudioSetupService(backend));

        using DoctorSession session = Session();
        _ = await session.InitializeAsync(new(1, root), default);
        PlanFixRequest request = new([], null, VisualStudio: new(["windows-cpp"], "vs-1"));
        FixPlan plan = await session.PlanFixAsync(request, default);
        Check(plan.Plan is null && plan.VisualStudioPlan is { Ready: false, Mode: "modify" } &&
              backend.Installs == 0, "Visual Studio planning must be read-only");

        using DoctorSession other = Session();
        _ = await other.InitializeAsync(new(1, root), default);
        await RejectAsync(() => other.ApplyFixAsync(new("foreign", plan.PlanId), default));
        Check(backend.Installs == 0, "VS fix plan cannot cross connections");

        // Same-instance state drift invalidates the single-use approval before running setup.exe.
        _ = backend.Installed.Add("Microsoft.VisualStudio.Component.VC.Tools.x86.x64");
        FixResult changed = await session.ApplyFixAsync(new("stale", plan.PlanId), default);
        Check(changed.Status == "plan_changed" && backend.Installs == 0 &&
              changed.VisualStudioVerification is not null, "VS plan must detect stale component state");
        await RejectAsync(() => session.ApplyFixAsync(new("replay", plan.PlanId), default));

        plan = await session.PlanFixAsync(request, default);
        FixResult applied = await session.ApplyFixAsync(new("apply", plan.PlanId), default);
        Check(applied.Status == "succeeded" && applied.VisualStudioInstallation?.Ready == true &&
              applied.VisualStudioVerification?.Ready == true && backend.Installs == 1,
            "Visual Studio install must re-probe and return the reviewed result");
        Check(progress.Any(p => p.OperationId == "apply" && p.Stage == "verifying") &&
              progress.Any(p => p.OperationId == "apply" && p.Stage == "finished"),
            "Visual Studio fix notifications missing");
        await RejectAsync(() => session.ApplyFixAsync(new("duplicate", plan.PlanId), default));
        Check(backend.Installs == 1, "Consumed VS plan cannot be retried");

        await RejectAsync(() => session.PlanFixAsync(request with { Profiles = ["cmake"] }, default));
        await RejectAsync(() => session.PlanFixAsync(request with { RunDirectory = root }, default));
        await session.DrainAsync();
        await other.DrainAsync();
        Console.WriteLine("Visual Studio Host Fix checks passed.");
    }

    private sealed class FakeBackend : IVisualStudioSetupBackend
    {
        public HashSet<string> Installed { get; } = new(StringComparer.Ordinal);
        public int Installs { get; private set; }

        public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VisualStudioInstance>>(
                [new VisualStudioInstance("vs-1", "Visual Studio Community", "18.0",
                    Path.Combine(Path.GetTempPath(), "vs-1"), null)]);

        public Task<bool> HasComponentAsync(string instanceId, string componentId, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(instanceId);
            ArgumentNullException.ThrowIfNull(componentId);
            return Task.FromResult(Installed.Contains(componentId));
        }

        public string? FindSetupExecutable() => Path.Combine(Path.GetTempPath(), "setup.exe");

        public Task<int> RunInstallerAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(arguments);
            Installs++;
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                if (arguments[i] == "--add")
                {
                    _ = Installed.Add(arguments[++i]);
                }
            }

            return Task.FromResult(0);
        }
    }
}
