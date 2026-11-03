using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record CampaignResult(
    int SchemaVersion,
    string Kind,
    string Status,
    int ExitCode,
    string? ArtifactDirectory);
