using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record CampaignWeakMemoryRequest(
    string SourceDirectory,
    string RelacyDirectory,
    string? ReportPath = null,
    string Protocol = "all",
    string Compiler = "c++",
    int TimeoutSeconds = 60);
