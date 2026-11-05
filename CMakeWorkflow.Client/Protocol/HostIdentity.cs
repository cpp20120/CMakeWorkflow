using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record HostIdentity(
    int ProtocolVersion,
    int ProcessId,
    string WorkspaceDirectory,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Capabilities
#pragma warning restore CA1819
,
    JsonElement Profiles);
