using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolingPlan(
    int SchemaVersion,
    ToolPlatform Platform,
    string[] Profiles,
    string InstallDirectory,
    PackageManagerKind PackageManager,
    bool Ready,
    List<ToolStatus> Tools,
    List<ToolingStep> Steps);
