using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

// Prepared native actions reuse the durable workflow engine and its operation API.
public sealed record TargetPrepareRequest(
    string RequestId,
    string ResultsDirectory,
    string Action,
    string BuildDirectory,
    string? Target = null,
    string? SecondTarget = null,
    string? Configuration = null,
    string[]? Arguments = null,
    string[]? SecondArguments = null,
    WorkflowToolchainBinding? Toolchain = null);
