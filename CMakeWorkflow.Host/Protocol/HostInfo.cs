using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.Tooling.Tools;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Protocol;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record HostInfo(
    int ProtocolVersion,
    int ProcessId,
    string WorkspaceDirectory,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Capabilities
#pragma warning restore CA1819
,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<ToolingProfileView> Profiles);
