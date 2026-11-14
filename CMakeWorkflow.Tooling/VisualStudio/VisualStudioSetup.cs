using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Tooling.Toolchains;

namespace CMakeWorkflow.Tooling.VisualStudio;

public sealed record VisualStudioSetupRequest(
    IReadOnlyList<string> Profiles,
    string? InstanceId = null,
    string? BootstrapperPath = null,
    string? InstallPath = null,
    bool BuildTools = false);

public sealed record VisualStudioSetupPlan(
    int SchemaVersion,
    string Mode,
    string? InstanceId,
    string InstallPath,
    string InstallerPath,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> MissingComponents,
    IReadOnlyList<string> Arguments,
    bool Ready,
    string ApprovalSha256,
    string? Diagnostic);

public sealed record VisualStudioSetupResult(
    int SchemaVersion,
    bool Ready,
    int InstallerExitCode,
    bool RestartRequired,
    VisualStudioSetupPlan Verification,
    string? Diagnostic);

public interface IVisualStudioSetupBackend
{
    Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(CancellationToken ct);
    Task<bool> HasComponentAsync(string instanceId, string componentId, CancellationToken ct);
    string? FindSetupExecutable();
    Task<int> RunInstallerAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct);
}

public static class VisualStudioProfileCatalog
{
    public const string WorkloadIde = "Microsoft.VisualStudio.Workload.NativeDesktop";
    public const string WorkloadBuildTools = "Microsoft.VisualStudio.Workload.VCTools";

    private static readonly IReadOnlyDictionary<string, string[]> Profiles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows-cpp"] =
            [
                "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "Microsoft.VisualStudio.Component.Windows11SDK.26100",
                "Microsoft.VisualStudio.Component.VC.CMake.Project",
                "Microsoft.VisualStudio.Component.VC.ASAN"
            ],
            ["windows-cpp-clang"] =
            [
                "Microsoft.VisualStudio.Component.VC.Llvm.Clang",
                "Microsoft.VisualStudio.Component.VC.Llvm.ClangToolset"
            ],
            ["windows-cpp-perf"] =
            [
                "Microsoft.VisualStudio.Component.VC.DiagnosticTools",
                "Microsoft.VisualStudio.Component.CppBuildInsights",
                "Microsoft.VisualStudio.Component.Windows11Sdk.WindowsPerformanceToolkit"
            ],
            ["windows-cpp-graphics"] = ["Microsoft.VisualStudio.Component.Graphics.Tools"]
        };

    public static IReadOnlyList<string> Names => [.. Profiles.Keys.Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> requested, bool buildTools)
    {
        ArgumentNullException.ThrowIfNull(requested);
        IReadOnlyList<string> names = requested.Count == 0 ? ["windows-cpp"] : requested;
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!Profiles.TryGetValue(name, out string[]? entries))
            {
                throw new ArgumentException($"Unknown Visual Studio profile '{name}'. Available: {string.Join(", ", Names)}.");
            }

            if (name.Equals("windows-cpp", StringComparison.OrdinalIgnoreCase))
            {
                _ = result.Add(buildTools ? WorkloadBuildTools : WorkloadIde);
            }

            result.UnionWith(entries);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }
}

/// <summary>
/// Read-only plan and explicit, stale-plan-checked execution. Does not change system state
/// during discovery, does not select an arbitrary VS instance, and never removes components.
/// </summary>
public sealed class VisualStudioSetupService(IVisualStudioSetupBackend backend)
{
    public async Task<VisualStudioSetupPlan> PlanAsync(VisualStudioSetupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profiles);
        if (request.InstallPath is not null && !Path.IsPathFullyQualified(request.InstallPath))
        {
            throw new ArgumentException("--install-path must be fully qualified.");
        }

        if (request.BootstrapperPath is not null && !Path.IsPathFullyQualified(request.BootstrapperPath))
        {
            throw new ArgumentException("--bootstrapper must be fully qualified.");
        }

        IReadOnlyList<VisualStudioInstance> installed = await backend.DiscoverAsync(ct);
        VisualStudioInstance? selected = null;
        if (!string.IsNullOrWhiteSpace(request.InstanceId))
        {
            selected = installed.SingleOrDefault(x => x.Id.Equals(request.InstanceId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException("Specified Visual Studio instance was not found.");
        }
        else if (!string.IsNullOrWhiteSpace(request.InstallPath))
        {
            selected = installed.SingleOrDefault(x => Path.GetFullPath(x.InstallationPath)
                .Equals(Path.GetFullPath(request.InstallPath), StringComparison.OrdinalIgnoreCase));
        }
        else if (installed.Count == 1)
        {
            selected = installed[0];
        }
        else if (installed.Count > 1)
        {
            throw new ArgumentException("More than one Visual Studio installation found. Specify --instance or --install-path.");
        }

        if (selected is null && (request.BootstrapperPath is null || request.InstallPath is null))
        {
            throw new ArgumentException("No matching Visual Studio installation. For initial install specify --bootstrapper and --install-path.");
        }

        if (selected is not null && request.BootstrapperPath is not null &&
            (request.InstallPath is null || !Path.GetFullPath(selected.InstallationPath)
                .Equals(Path.GetFullPath(request.InstallPath), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("--bootstrapper may only target its explicit installation path.");
        }

        bool buildTools = selected is not null
            ? selected.Name.Contains("Build Tools", StringComparison.OrdinalIgnoreCase)
              || selected.InstallationPath.Contains("BuildTools", StringComparison.OrdinalIgnoreCase)
            : request.BuildTools;
        IReadOnlyList<string> requestedProfiles = request.Profiles.Count == 0
            ? ["windows-cpp"] : request.Profiles;
        string[] profiles = [.. requestedProfiles.Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        IReadOnlyList<string> components = VisualStudioProfileCatalog.Resolve(profiles, buildTools);
        List<string> missing = [];
        foreach (string component in components)
        {
            if (selected is null || !await backend.HasComponentAsync(selected.Id, component, ct))
            {
                missing.Add(component);
            }
        }

        string mode = selected is null ? "install" : "modify";
        string path = selected?.InstallationPath ?? Path.GetFullPath(request.InstallPath!);
        string installer = selected is null ? Path.GetFullPath(request.BootstrapperPath!) : backend.FindSetupExecutable() ?? "";
        bool ready = selected is not null && missing.Count == 0;
        List<string> arguments = [];
        if (!ready)
        {
            if (selected is not null)
            {
                arguments.Add("modify");
            }

            arguments.AddRange(["--installPath", path]);
            foreach (string component in missing)
            {
                arguments.AddRange(["--add", component]);
            }

            arguments.AddRange(["--passive", "--norestart"]);
            if (selected is null)
            {
                arguments.Add("--wait"); // Bootstrapper only; setup.exe modify does not accept this.
            }
        }

        string material = string.Join("\n", [
            mode, selected?.Id ?? "", selected?.Version ?? "", path, installer,
            buildTools ? "buildtools" : "ide", string.Join(",", profiles),
            string.Join(",", components), string.Join(",", missing), string.Join(" ", arguments)
        ]);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        string? diagnostic = !ready && installer.Length == 0
            ? "Visual Studio Installer (setup.exe) was not found."
            : null;
        return new(1, mode, selected?.Id, path, installer, profiles, components, missing,
            arguments, ready, hash, diagnostic);
    }

    public async Task<VisualStudioSetupResult> ApplyAsync(VisualStudioSetupRequest request,
        string approvedSha256, CancellationToken ct)
    {
        VisualStudioSetupPlan before = await PlanAsync(request, ct);
        if (!before.ApprovalSha256.Equals(approvedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Visual Studio plan changed. Run plan again and review the new SHA-256.");
        }

        if (before.Ready)
        {
            return new(1, true, 0, false, before, null);
        }

        if (before.InstallerPath.Length == 0)
        {
            throw new InvalidOperationException(before.Diagnostic);
        }

        int exit = await backend.RunInstallerAsync(before.InstallerPath, before.Arguments, ct);
        // 3010 is Windows ERROR_SUCCESS_REBOOT_REQUIRED.
        bool reboot = exit == 3010;
        VisualStudioSetupPlan verified = await PlanAsync(request, ct);
        bool successful = (exit is 0 or 3010) && verified.Ready;
        string? diagnostic = successful ? null : reboot
            ? "Installer requested reboot. Reboot, then run doctor; no automatic retry was performed."
            : exit != 0 ? $"Visual Studio Installer returned {exit}."
            : "Installer exited successfully but requested components are still missing.";
        return new(1, successful, exit, reboot, verified, diagnostic);
    }
}
