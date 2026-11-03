using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record CampaignExecutionResult(
    int SchemaVersion,
    string Kind,
    string Status,
    int ExitCode,
    string? ArtifactDirectory);
