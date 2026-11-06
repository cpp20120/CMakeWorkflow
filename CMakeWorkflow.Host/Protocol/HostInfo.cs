using CMakeWorkflow.Tooling.Tools;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Protocol;
#pragma warning restore IDE0130

public sealed record HostInfo(
    int ProtocolVersion,
    int ProcessId,
    string WorkspaceDirectory,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Capabilities
#pragma warning restore CA1819
,

List<ToolingProfileView> Profiles);
