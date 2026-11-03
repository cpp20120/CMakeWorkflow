using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record HistoryRunsRequest(
    string DatabasePath,
    int Limit = 50,
    string? Status = null,
    string? Since = null,
    string? Until = null,
    string? Workflow = null,
    string? Variant = null);
