using System.Text.Json.Serialization;

namespace CMakeWorkflow.Client;

// These are SDK wire DTOs, not references to Host/Tooling assemblies.
// Enum strings match the Host's AOT-safe JsonStringEnumConverter contracts.
[JsonConverter(typeof(JsonStringEnumConverter<HostToolPlatform>))]
public enum HostToolPlatform { Linux, MacOS, Windows }

[JsonConverter(typeof(JsonStringEnumConverter<HostPackageManagerKind>))]
public enum HostPackageManagerKind { Auto, None, Apt, Pacman, Dnf, Brew, Winget }

public sealed record HostDoctorOptions(string OperationId, string[] Profiles, string? InstallDirectory = null);
public sealed record HostPlanFixOptions(string[] Profiles, string? InstallDirectory = null,
    HostPackageManagerKind PackageManager = HostPackageManagerKind.Auto,
    bool AllowFallback = true, string? RunDirectory = null);
public sealed record HostApplyFixOptions(string OperationId, string PlanId);

public sealed record HostToolStatus(string Id, string DisplayName, bool Installed, string? Command,
    string? Version, string? MinimumVersion, string? Diagnostic);
public sealed record HostToolingStep(string Tool, string Method, string? Executable,
    string[] Arguments, string? Source, string? Diagnostic);
public sealed record HostToolingPlan(int SchemaVersion, HostToolPlatform Platform, string[] Profiles,
    string InstallDirectory, HostPackageManagerKind PackageManager, bool Ready,
    List<HostToolStatus> Tools, List<HostToolingStep> Steps);
public sealed record HostToolingResult(int SchemaVersion, bool Success,
    string[] Ready, string[] Failed, string[] Diagnostics);
public sealed record HostFixPlan(int SchemaVersion, string PlanId, DateTimeOffset ExpiresAt,
    HostToolingPlan Plan, string? RunDirectory = null, string? RunSnapshot = null);
public sealed record HostFixResult(int SchemaVersion, string Status,
    HostToolingResult? Installation, HostToolingPlan? Verification,
    string[] Diagnostics, bool ReadyToResume = false);
