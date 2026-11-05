#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

// Deliberately separate machine observations from mutating tool installation.
// A plan never modifies global kernel or CPU settings.
public sealed record HostRequirements(
    int SchemaVersion,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] ToolProfiles
#pragma warning restore CA1819
,
    int? MaxPerfEventParanoid = null,
    string? CpuGovernor = null,
    string? WindowsPowerScheme = null,
    bool RequireWindowsMsvc = false,
    bool RequireWindowsSdk = false,
    bool RequireWindowsWpr = false);
