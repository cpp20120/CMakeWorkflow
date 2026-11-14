using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.VisualStudio;

namespace CMakeWorkflow.Tooling.Tests;

internal static class VisualStudioSetupTests
{
    public static async Task RunAsync()
    {
        FakeBackend backend = new();
        VisualStudioSetupService service = new(backend);
        VisualStudioSetupRequest request = new(["windows-cpp", "windows-cpp-perf"], "vs-1");
        VisualStudioSetupPlan plan = await service.PlanAsync(request, default);
        Check(plan.Mode == "modify" && plan.MissingComponents.Contains(
            "Microsoft.VisualStudio.Component.Windows11Sdk.WindowsPerformanceToolkit"),
            "WPA/WPR belong to the opt-in perf profile");
        Check(plan.Arguments[0] == "modify" && !plan.Arguments.Contains("--wait"),
            "setup.exe modify must not use bootstrapper-only --wait");
        Check(plan.ApprovalSha256.Length == 64 && !plan.Ready, "VS plan hash and readiness");

        bool rejected = false;
        try
        {
            _ = await service.ApplyAsync(request, new string('0', 64), default);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check(rejected && backend.Installs == 0, "unapproved installation must not run");
        VisualStudioSetupResult applied = await service.ApplyAsync(request, plan.ApprovalSha256, default);
        Check(applied.Ready && backend.Installs == 1 && applied.Verification.Ready,
            "approved component installation and re-probe");
        Check((await service.PlanAsync(request, default)).Ready, "idempotent repeated plan");

        backend.Installed.Clear();
        VisualStudioSetupPlan stale = await service.PlanAsync(request, default);
        _ = backend.Installed.Add("Microsoft.VisualStudio.Component.VC.Tools.x86.x64");
        rejected = false;
        try
        {
            _ = await service.ApplyAsync(request, stale.ApprovalSha256, default);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Check(rejected && backend.Installs == 1, "changed component state invalidates plan approval");

        FakeBackend fresh = new() { Instances = [] };
        VisualStudioSetupService newService = new(fresh);
        VisualStudioSetupRequest firstInstall = new(["windows-cpp"],
            BootstrapperPath: Path.Combine(Path.GetTempPath(), "vs_community.exe"),
            InstallPath: Path.Combine(Path.GetTempPath(), "cmwf-new-vs"));
        VisualStudioSetupPlan newPlan = await newService.PlanAsync(firstInstall, default);
        Check(newPlan.Mode == "install" && newPlan.Arguments.Contains("--wait"),
            "new installation requires edition bootstrapper with --wait");
        VisualStudioSetupResult firstResult = await newService.ApplyAsync(firstInstall, newPlan.ApprovalSha256, default);
        Check(firstResult.Ready && firstResult.Verification.Ready, "first-time install and re-probe");

        Check(!VisualStudioProfileCatalog.Resolve(["windows-cpp"], false).Contains(
            VisualStudioProfileCatalog.WorkloadBuildTools), "IDE vs Build Tools workload isolation");
        Check(VisualStudioProfileCatalog.Resolve(["windows-cpp"], true).Contains(
            VisualStudioProfileCatalog.WorkloadBuildTools), "build tools workload selection");
        Console.WriteLine("Visual Studio setup plan/apply checks passed.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
    }

    private sealed class FakeBackend : IVisualStudioSetupBackend
    {
        public List<VisualStudioInstance> Instances { get; set; } =
            [new("vs-1", "Visual Studio Community", "18.0", Path.Combine(Path.GetTempPath(), "vs-1"), null)];
        public HashSet<string> Installed { get; } = new(StringComparer.Ordinal);
        public int Installs { get; private set; }

        public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VisualStudioInstance>>(Instances);

        public Task<bool> HasComponentAsync(string instanceId, string componentId, CancellationToken ct) =>
            Task.FromResult(Installed.Contains(componentId));

        public string? FindSetupExecutable() => Path.Combine(Path.GetTempPath(), "setup.exe");

        public Task<int> RunInstallerAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Installs++;
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                if (arguments[i] == "--add")
                {
                    _ = Installed.Add(arguments[++i]);
                }
            }

            if (Instances.Count == 0)
            {
                string installPath = "";
                for (int i = 0; i + 1 < arguments.Count; i++)
                {
                    if (arguments[i] == "--installPath")
                    {
                        installPath = arguments[i + 1];
                        break;
                    }
                }

                Instances.Add(new("vs-2", "Visual Studio Community", "18.0", installPath, null));
            }

            return Task.FromResult(0);
        }
    }
}
