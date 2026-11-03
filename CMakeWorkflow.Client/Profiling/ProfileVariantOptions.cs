using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record ProfileVariantOptions(
    string Name,
    string BuildDirectory,
    WorkflowToolchainBinding? Toolchain = null);
