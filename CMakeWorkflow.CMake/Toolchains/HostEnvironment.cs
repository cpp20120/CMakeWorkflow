using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow;

public sealed record HostEnvironment(
    string? HostId,
    string? CpuModel,
    string Architecture,
    int LogicalProcessors,
    int AvailableProcessors,
    int? PhysicalPackages,
    int? PhysicalCores,
    string OperatingSystem,
    string KernelVersion,
    string? Affinity,
    string? Governor,
    string Fingerprint);
