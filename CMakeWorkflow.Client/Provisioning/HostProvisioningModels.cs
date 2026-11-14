using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Client.Provisioning;

// These are SDK wire DTOs, not references to Host/Tooling assemblies.
// Enum strings match the Host's AOT-safe JsonStringEnumConverter contracts.
[JsonConverter(typeof(JsonStringEnumConverter<HostToolPlatform>))]
public enum HostToolPlatform { Linux, MacOS, Windows }

[JsonConverter(typeof(JsonStringEnumConverter<HostPackageManagerKind>))]
public enum HostPackageManagerKind { Auto, None, Apt, Pacman, Dnf, Brew, Winget }

public sealed record HostDoctorOptions(string OperationId,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
, string? InstallDirectory = null);
public sealed record HostPlanFixOptions(
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
, string? InstallDirectory = null,
    HostPackageManagerKind PackageManager = HostPackageManagerKind.Auto,
    bool AllowFallback = true, string? RunDirectory = null,
    HostVisualStudioSetupOptions? VisualStudio = null);
public sealed record HostApplyFixOptions(string OperationId, string PlanId);

public sealed record HostToolStatus(string Id, string DisplayName, bool Installed, string? Command,
    string? Version, string? MinimumVersion, string? Diagnostic);
public sealed record HostToolingStep(string Tool, string Method, string? Executable,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Arguments
#pragma warning restore CA1819
, string? Source, string? Diagnostic);
[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Provisioning.HostToolingPlan(Tools, Steps) participates in CMakeWorkflow.Client.Provisioning.HostProvisioningService.DoctorAsync; retain these concrete collection parameter types.")]
public sealed record HostToolingPlan(int SchemaVersion, HostToolPlatform Platform,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
,
    string InstallDirectory, HostPackageManagerKind PackageManager, bool Ready,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Provisioning.HostToolingPlan.Tools (List<HostToolStatus>) is exposed through CMakeWorkflow.Client.Provisioning.HostProvisioningService.DoctorAsync; retain this Client SDK property signature.")]
List<HostToolStatus> Tools, [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Provisioning.HostToolingPlan.Steps (List<HostToolingStep>) is exposed through CMakeWorkflow.Client.Provisioning.HostProvisioningService.DoctorAsync; retain this Client SDK property signature.")]
List<HostToolingStep> Steps);
public sealed record HostToolingResult(int SchemaVersion, bool Success,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Ready
#pragma warning restore CA1819
,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Failed
#pragma warning restore CA1819
,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Diagnostics
#pragma warning restore CA1819
);
public sealed record HostVisualStudioSetupOptions(IReadOnlyList<string> Profiles,
    string? InstanceId = null, string? BootstrapperPath = null, string? InstallPath = null,
    bool BuildTools = false);
public sealed record HostVisualStudioSetupPlan(int SchemaVersion, string Mode, string? InstanceId,
    string InstallPath, string InstallerPath, IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Components, IReadOnlyList<string> MissingComponents,
    IReadOnlyList<string> Arguments, bool Ready, string ApprovalSha256, string? Diagnostic);
public sealed record HostVisualStudioSetupResult(int SchemaVersion, bool Ready, int InstallerExitCode,
    bool RestartRequired, HostVisualStudioSetupPlan Verification, string? Diagnostic);
public sealed record HostFixPlan(int SchemaVersion, string PlanId, DateTimeOffset ExpiresAt,
    HostToolingPlan? Plan, string? RunDirectory = null, string? RunSnapshot = null,
    HostVisualStudioSetupPlan? VisualStudioPlan = null);
public sealed record HostFixResult(int SchemaVersion, string Status,
    HostToolingResult? Installation, HostToolingPlan? Verification,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Diagnostics
#pragma warning restore CA1819
, bool ReadyToResume = false,
    HostVisualStudioSetupResult? VisualStudioInstallation = null,
    HostVisualStudioSetupPlan? VisualStudioVerification = null);
