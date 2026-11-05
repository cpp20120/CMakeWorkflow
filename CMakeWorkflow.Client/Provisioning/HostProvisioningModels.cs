using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

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
    bool AllowFallback = true, string? RunDirectory = null);
public sealed record HostApplyFixOptions(string OperationId, string PlanId);

public sealed record HostToolStatus(string Id, string DisplayName, bool Installed, string? Command,
    string? Version, string? MinimumVersion, string? Diagnostic);
public sealed record HostToolingStep(string Tool, string Method, string? Executable,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Arguments
#pragma warning restore CA1819
, string? Source, string? Diagnostic);
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record HostToolingPlan(int SchemaVersion, HostToolPlatform Platform,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
,
    string InstallDirectory, HostPackageManagerKind PackageManager, bool Ready,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HostToolStatus> Tools, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
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
public sealed record HostFixPlan(int SchemaVersion, string PlanId, DateTimeOffset ExpiresAt,
    HostToolingPlan Plan, string? RunDirectory = null, string? RunSnapshot = null);
public sealed record HostFixResult(int SchemaVersion, string Status,
    HostToolingResult? Installation, HostToolingPlan? Verification,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Diagnostics
#pragma warning restore CA1819
, bool ReadyToResume = false);
