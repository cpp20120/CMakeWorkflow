using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record PrepareTargetOptions(
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
