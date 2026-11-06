// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.CMake.Toolchains;
#pragma warning restore IDE0130

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
