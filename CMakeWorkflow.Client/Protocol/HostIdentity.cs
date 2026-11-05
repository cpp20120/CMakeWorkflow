using System.Text.Json;

namespace CMakeWorkflow.Client.Protocol;

public sealed record HostIdentity(
    int ProtocolVersion,
    int ProcessId,
    string WorkspaceDirectory,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Capabilities
#pragma warning restore CA1819
,
    JsonElement Profiles);
