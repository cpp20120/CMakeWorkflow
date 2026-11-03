using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

// Deliberately separate machine observations from mutating tool installation.
// A plan never modifies global kernel or CPU settings.
public sealed record HostRequirements(
    int SchemaVersion,
    string[] ToolProfiles,
    int? MaxPerfEventParanoid = null,
    string? CpuGovernor = null,
    string? WindowsPowerScheme = null,
    bool RequireWindowsMsvc = false,
    bool RequireWindowsSdk = false,
    bool RequireWindowsWpr = false);
