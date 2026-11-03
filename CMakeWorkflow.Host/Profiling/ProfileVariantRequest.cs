using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record ProfileVariantRequest(
    string Name,
    string BuildDirectory,
    WorkflowToolchainBinding? Toolchain = null);
