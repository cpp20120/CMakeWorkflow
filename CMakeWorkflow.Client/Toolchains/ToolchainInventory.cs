using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record ToolchainInventory(
    int SchemaVersion,
    string Platform,
    List<VisualStudioInstance> VisualStudio,
    List<ToolchainCandidate> Candidates,
    List<string> Diagnostics);
