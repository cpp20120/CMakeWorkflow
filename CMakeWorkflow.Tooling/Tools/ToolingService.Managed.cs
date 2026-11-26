using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling.Tools;

public sealed partial class ToolingService
{
    internal Func<string, ManagedToolRelease?> FindManagedRelease { get; init; } = ToolCatalog.ManagedRelease;
    internal Architecture ManagedArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    private async Task<ToolingPlan> PlanManagedAsync(IReadOnlyList<string> requestedProfiles,
        PackageManagerKind manager, string? installDirectory, ToolResolutionPolicy policy,
        Func<ToolStatus, Task>? onTool, CancellationToken ct)
    {
        if (!Enum.IsDefined(policy) || manager is not (PackageManagerKind.Auto or PackageManagerKind.None)
                                    || installDirectory is not null)
        {
            throw new ArgumentException("Managed tooling accepts only package manager 'none' and uses CMAKEWORKFLOW_TOOL_STORE, not --install-dir.");
        }

        string directory = ManagedToolStore.GetRoot();
        string[] profiles = requestedProfiles.Count == 0 ? ["cmake-ninja"] :
            [.. requestedProfiles.Select(x => ToolCatalog.CanonicalProfileId(x.Trim())).Distinct(StringComparer.Ordinal)];
        IReadOnlyList<ToolDefinition> definitions = ToolCatalog.ResolveProfiles(profiles, runtime.Platform);
        if (definitions.Any(tool => tool.Id != ManagedCMakeFormatter.Id) &&
            !ManagedToolStore.Supports(runtime.Platform, ManagedArchitecture))
        {
            throw new PlatformNotSupportedException("The selected managed toolchain profiles require Linux x86-64.");
        }
        if (profiles.Contains("linux-clang", StringComparer.Ordinal))
        {
            ManagedLinuxSdk.RequireHostAbi();
        }

        List<ToolStatus> statuses = new(definitions.Count);
        List<ToolingStep> steps = [];
        Dictionary<ManagedToolRelease, bool> verified = [];
        foreach (ToolDefinition tool in definitions)
        {
            ct.ThrowIfCancellationRequested();
            ToolStatus? system = policy == ToolResolutionPolicy.AllowSystem
                ? await ProbeAsync(tool.Id == "ninja" ? tool with { MinimumVersion = new(1, 10) } : tool, "", ct)
                : null;
            ToolStatus status;
            if (system is { Installed: true })
            {
                status = system with { Origin = "system", Resolution = "compatible" };
            }
            else if ((tool.Id == ManagedCMakeFormatter.Id
                ? ManagedCMakeFormatter.ForHost(runtime.Platform, ManagedArchitecture)
                : FindManagedRelease(tool.Id)) is { } release)
            {
                if (!verified.TryGetValue(release, out bool installed))
                {
                    installed = await ManagedToolStore.IsInstalledAsync(directory, release, ct);
                    verified.Add(release, installed);
                }

                status = new(tool.Id, tool.DisplayName, installed,
                    installed ? ManagedToolStore.ToolPath(directory, release, tool.Id) : null,
                    release.Version, tool.MinimumVersion?.ToString(), system?.Diagnostic)
                {
                    Origin = "managed",
                    Resolution = installed ? "compatible" : "downloadable",
                    Sha256 = release.Sha256,
                    SystemResolution = system is null ? null : system.Command is null ? "missing" : "incompatible",
                    SystemVersion = system?.Version
                };
                if (!installed && !steps.Any(step => step.Release == release))
                {
                    steps.Add(new(release.Id, "managed-release", null, [], release.Source, null) { Release = release });
                }
            }
            else
            {
                bool sdk = tool.Id is "cxx" or "clangxx";
                status = new(tool.Id, tool.DisplayName, false, system?.Command, system?.Version,
                    tool.MinimumVersion?.ToString(), sdk
                        ? "A compatible external compiler and Linux SDK are required. Use AllowSystem with an installed compiler or select cmake-ninja."
                        : tool.Id == ManagedCMakeFormatter.Id
                            ? "No compatible cmakefmt release for this OS, architecture and Linux ABI. " + system?.Diagnostic
                            : system?.Diagnostic ?? "No managed release is available for this tool.")
                {
                    Resolution = sdk ? "external-sdk" : system?.Command is not null ? "incompatible" : "missing"
                };
            }

            statuses.Add(status);
            if (onTool is not null)
            {
                await onTool(status);
            }
        }

        return new ToolingPlan(1, runtime.Platform, profiles, directory, PackageManagerKind.None,
            statuses.All(s => s.Installed), statuses, [.. steps.OrderBy(step => step.Tool != "sysroot")])
        {
            ResolutionPolicy = policy
        };
    }

    private async Task<ToolingResult> ExecuteManagedAsync(ToolingPlan plan, ToolResolutionPolicy policy,
        Func<ToolingExecutionProgress, Task>? onProgress, CancellationToken ct)
    {
        ToolingPlan current = await PlanManagedAsync(plan.Profiles, PackageManagerKind.None, null, policy, null, ct);
        if (ToolingPlanApproval.Compute(plan) != ToolingPlanApproval.Compute(current))
        {
            throw new InvalidDataException("Managed tool plan changed after review. Review a new plan.");
        }

        if (installer is not VerifiedCMakeInstaller verified)
        {
            throw new InvalidOperationException("Managed tools require the verified release installer.");
        }

        ManagedToolStore store = new(verified);
        List<string> diagnostics = [];
        foreach (ToolingStep step in current.Steps)
        {
            ManagedToolRelease release = step.Release!;
            try
            {
                if (onProgress is not null)
                {
                    await onProgress(new(step.Tool, "managed_install", release.Version + " " + release.Sha256));
                }

                await store.InstallAsync(current.InstallDirectory, release, runtime, ct);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException
                                          or UnauthorizedAccessException or TimeoutException or Win32Exception)
            {
                diagnostics.Add($"{step.Tool}: {error.Message}");
                break;
            }
        }

        ToolingPlan checkedPlan = await PlanManagedAsync(current.Profiles, PackageManagerKind.None, null, policy, null, ct);
        diagnostics.AddRange(checkedPlan.Tools.Where(tool => !tool.Installed && tool.Diagnostic is not null)
            .Select(tool => $"{tool.Id}: {tool.Diagnostic}"));
        return new(1, checkedPlan.Ready && diagnostics.Count == 0,
            [.. checkedPlan.Tools.Where(tool => tool.Installed).Select(tool => tool.Id)],
            [.. checkedPlan.Tools.Where(tool => !tool.Installed).Select(tool => tool.Id)], [.. diagnostics]);
    }
}
