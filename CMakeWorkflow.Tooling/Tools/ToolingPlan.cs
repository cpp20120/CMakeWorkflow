
namespace CMakeWorkflow.Tooling.Tools;

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
