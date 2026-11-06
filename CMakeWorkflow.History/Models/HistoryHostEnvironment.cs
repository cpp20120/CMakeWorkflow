// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Models;
#pragma warning restore IDE0130

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
