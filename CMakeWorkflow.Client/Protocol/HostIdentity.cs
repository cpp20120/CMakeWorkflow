using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record HostIdentity(
    int ProtocolVersion,
    int ProcessId,
    string WorkspaceDirectory,
    string[] Capabilities,
    JsonElement Profiles);
