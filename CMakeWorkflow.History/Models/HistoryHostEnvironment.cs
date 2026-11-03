using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.History;

public sealed record HistoryHostEnvironment(
    string? HostId,
    string? CpuModel,
    string? Architecture,
    int? LogicalProcessors,
    int? AvailableProcessors,
    int? PhysicalPackages,
    int? PhysicalCores,
    string? OperatingSystem,
    string? KernelVersion,
    string? Affinity,
    string? Governor,
    string? Fingerprint);
