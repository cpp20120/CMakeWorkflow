using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

// Independent SDK models: the client references only Contracts, never Host.
public sealed record ToolchainCandidate(
    string Id,
    string Role,
    string Flavor,
    string Provider,
    string Path,
    string? Version = null,
    string? VisualStudioInstanceId = null,
    string? EnvironmentProfileId = null);
