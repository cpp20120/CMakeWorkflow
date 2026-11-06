
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed record ToolingPlan(
    int SchemaVersion,
    ToolPlatform Platform,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
,
    string InstallDirectory,
    PackageManagerKind PackageManager,
    bool Ready,

List<ToolStatus> Tools,

List<ToolingStep> Steps);
