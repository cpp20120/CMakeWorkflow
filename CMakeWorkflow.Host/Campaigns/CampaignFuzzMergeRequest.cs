using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record CampaignFuzzMergeRequest(
    string RunDirectory,
    string? OutputDirectory = null,
    bool Coverage = false);
