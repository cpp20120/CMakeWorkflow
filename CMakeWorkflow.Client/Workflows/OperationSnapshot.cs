using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record OperationSnapshot(
    string WorkspaceId,
    string OperationId,
    string ClientRequestId,
    string Kind,
    string State,
    string ManifestSha256,
    string ManifestBaseDirectory);
