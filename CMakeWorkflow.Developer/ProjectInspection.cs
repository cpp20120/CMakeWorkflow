using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Provisioning;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Developer;

public static partial class DeveloperLifecycle
{
    private const string ProjectManifest = "cmwf.project.json";

    public static Task<DiscoveredProject> DiscoverProjectAsync(ProjectLocation location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        string start = Path.GetFullPath(location.StartingDirectory);
        bool exact = location.ProjectDirectory is not null;
        DirectoryInfo directory = new(exact ? Path.GetFullPath(location.ProjectDirectory!, start) : start);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException(directory.FullName);
        }
        for (DirectoryInfo? ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
        {
            ct.ThrowIfCancellationRequested();
            if (ancestor.LinkTarget is not null || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Project discovery cannot cross a directory link: " + ancestor.FullName);
            }
        }
        for (DirectoryInfo? candidate = directory; candidate is not null; candidate = exact ? null : candidate.Parent)
        {
            ct.ThrowIfCancellationRequested();
            string manifest = BuildVariantConfiguration.CheckedPath(candidate.FullName, ProjectManifest);
            if (File.Exists(manifest))
            {
                return Task.FromResult(new DiscoveredProject(candidate.FullName, manifest));
            }
        }
        throw new FileNotFoundException("No cmwf.project.json found for " + directory.FullName);
    }

    public static Task<ProjectInspection> InspectProjectAsync(ProjectLocation location, ConfigurationSelection selection,
        CancellationToken ct = default) => PlanRestoreAsync(new(location) { Configuration = selection }, ct);

    public static async Task<ProjectInspection> PlanRestoreAsync(RestoreRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DiscoveredProject project = await DiscoverProjectAsync(request.Location, ct);
        ProjectLockPlan sdkPlan = await PlanSdkAsync(project.Directory, ct);
        using JsonDocument sdkDocument = JsonDocument.Parse(sdkPlan.Json);
        SdkDescriptor sdk = CopySdk(ProjectSdkResolution.Descriptor(sdkDocument.RootElement));
        List<DeveloperDiagnostic> diagnostics = [];
        ConfigurationPlan? configuration = null;
        if (!await HasLockAsync(project.Directory, sdkPlan, ct))
        {
            diagnostics.Add(new(DeveloperDiagnosticCode.SdkLockApprovalRequired,
                "The SDK lock is missing, stale, or modified.", "restore --sdk-plan-sha " + sdkPlan.Sha256));
        }
        else if (!File.Exists(BuildVariantConfiguration.CheckedPath(project.Directory, "cmwf.config.json")))
        {
            diagnostics.Add(new(DeveloperDiagnosticCode.ConfigurationUnavailable, "This project has no variant configuration.",
                "Use its existing CMake presets or workflow commands."));
        }
        else
        {
            configuration = await PlanConfigurationAsync(project.Directory, request.Configuration, ct);
            if (request.Configuration.Variant is { } selector)
            {
                ProjectVariant[] selected = [.. configuration.Variants.Where(variant => variant.Name == selector || variant.Id == selector)];
                configuration = selected.Length != 0 ? configuration with { Variants = Array.AsReadOnly(selected) } :
                    throw new InvalidDataException("unknown-workflow-variant: " + selector);
            }
            if (!await HasLockAsync(project.Directory, configuration.Lock, ct))
            {
                diagnostics.Add(new(DeveloperDiagnosticCode.ConfigurationLockApprovalRequired,
                    "The selected configuration lock is missing or modified.",
                    "restore --configuration-plan-sha " + configuration.Lock.Sha256));
            }
        }

        foreach (SdkRequirement requirement in sdk.Requirements)
        {
            diagnostics.Add(new(DeveloperDiagnosticCode.ExternalDependencyUnverified,
                requirement.Name + " (" + requirement.Admission + ") must be verified on the execution host."));
        }
        ProjectLockPlan? dependencies = await PlanDependenciesAsync(project.Directory, diagnostics, request.ProbeLocalTools, ct);
        ToolingPlan? tooling = null;
        if (request.ProbeLocalTools && configuration is not null)
        {
            tooling = await InspectLocalToolsAsync(project.Directory, sdk, configuration, diagnostics, ct);
        }
        else
        {
            diagnostics.Add(new(DeveloperDiagnosticCode.HostEnvironmentUnverified,
                "Execution-host tools and environment are checked by Host admission and configure."));
        }
        return new(project, sdk, sdkPlan, configuration, diagnostics.AsReadOnly()) { LocalTooling = tooling, VcpkgPlan = dependencies };
    }

    /// <summary>Probes the initialized Host using only the selected SDK and environment policy.</summary>
    public static Task<HostToolingPlan> InspectHostToolsAsync(CMakeWorkflowClient client, ProjectInspection inspection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(inspection);
        bool managed = inspection.Configuration?.Variants.Any(variant => variant.EnvironmentLockSha256 is not null) == true;
        return client.Provisioning.DoctorAsync(new(Guid.NewGuid().ToString("N"), ToolProfiles(inspection.Sdk, managed))
        {
            ResolutionPolicy = managed ? HostToolResolutionPolicy.ManagedOnly : null
        }, ct);
    }

    private static string[] ToolProfiles(SdkDescriptor sdk, bool managed) =>
        sdk.Capabilities.Packaging ? [managed ? "linux-clang" : "native", "packaging"] : [managed ? "linux-clang" : "native"];

    private static async Task<ToolingPlan> InspectLocalToolsAsync(string root, SdkDescriptor sdk, ConfigurationPlan configuration,
        List<DeveloperDiagnostic> diagnostics, CancellationToken ct)
    {
        string? environment = configuration.Variants[0].EnvironmentLockSha256;
        if (environment is not null)
        {
            try
            {
                await ManagedEnvironmentLocks.VerifyAsync(environment, ct);
            }
            catch (InvalidDataException error)
            {
                diagnostics.Add(new(DeveloperDiagnosticCode.EnvironmentLockUnavailable, error.Message,
                    "tooling verify --environment-lock " + environment));
            }
        }
        ToolingService service = new(new SystemToolingRuntime(root), new VerifiedCMakeInstaller());
        ToolingPlan plan = await service.PlanAsync(ToolProfiles(sdk, environment is not null), PackageManagerKind.None,
            allowFallback: false, installDirectory: null, ct: ct,
            resolutionPolicy: environment is null ? null : ToolResolutionPolicy.ManagedOnly);
        if (!plan.Ready)
        {
            string command = environment is null ? "tooling plan --profile native" : "project tooling plan";
            diagnostics.Add(new(DeveloperDiagnosticCode.ToolingUnavailable, "Required local tools are missing or incompatible.", command));
        }
        return plan;
    }

    private static async Task<ProjectLockPlan?> PlanDependenciesAsync(string root, List<DeveloperDiagnostic> diagnostics,
        bool verifyLocalReceipt, CancellationToken ct)
    {
        if (!File.Exists(BuildVariantConfiguration.CheckedPath(root, "vcpkg.json")))
        {
            return null;
        }
        _ = BuildVariantConfiguration.CheckedPath(root, "vcpkg-configuration.json");
        byte[] bytes = VcpkgProjects.Serialize(await VcpkgProjects.PlanAsync(root, ct));
        string digest = BuildVariantConfiguration.Hash(bytes);
        try
        {
            if (verifyLocalReceipt)
            {
                _ = await VcpkgProjects.ResolveAsync(digest, ct);
            }
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException)
        {
            diagnostics.Add(new(DeveloperDiagnosticCode.DependencyPlanApprovalRequired, error.Message,
                "Run from the project root: tooling vcpkg install . --yes --plan-sha " + digest));
        }
        return new(digest, "vcpkg.json", bytes);
    }

    public static async Task<ProjectInspection> RestoreAsync(RestoreRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // SDK approval can change configuration inputs, so review these two changes independently.
        if (request.SdkApprovalSha256 is not null && request.ConfigurationApprovalSha256 is not null)
        {
            throw new ArgumentException("Apply the SDK approval before reviewing the configuration plan.", nameof(request));
        }
        DiscoveredProject project = await DiscoverProjectAsync(request.Location, ct);
        if (request.SdkApprovalSha256 is { } sdk)
        {
            _ = await LockSdkAsync(project.Directory, sdk, ct);
        }
        if (request.ConfigurationApprovalSha256 is { } configuration)
        {
            _ = await LockConfigurationAsync(project.Directory, request.Configuration, configuration, ct);
        }
        return await PlanRestoreAsync(request, ct);
    }
}
