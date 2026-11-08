using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Provisioning;

public sealed record PlanFixRequest(

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
,
    string? InstallDirectory,
    PackageManagerKind PackageManager = PackageManagerKind.Auto,
    bool AllowFallback = true,
    string? RunDirectory = null);
