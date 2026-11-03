using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record PlanFixRequest(
    string[] Profiles,
    string? InstallDirectory,
    PackageManagerKind PackageManager = PackageManagerKind.Auto,
    bool AllowFallback = true,
    string? RunDirectory = null);
