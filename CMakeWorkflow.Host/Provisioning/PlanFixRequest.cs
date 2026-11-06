using CMakeWorkflow.Tooling.Tools;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Provisioning;
#pragma warning restore IDE0130

public sealed record PlanFixRequest(

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
,
    string? InstallDirectory,
    PackageManagerKind PackageManager = PackageManagerKind.Auto,
    bool AllowFallback = true,
    string? RunDirectory = null);
