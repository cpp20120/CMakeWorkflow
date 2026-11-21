namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Requests current experiment capacity and optional managed toolchain validation.</summary>
public sealed record ExperimentResourceRequest(bool ManagedClang);

/// <summary>Host process CPU capacity and the resolved managed toolchain, if requested.</summary>
/// <remarks>AvailableProcessors is the processor count visible to .NET. It does not measure load or reserve CPUs.</remarks>
public sealed record ExperimentHostResources(string Architecture, int AvailableProcessors,
    string? ManagedCompilerVersion, string? ManagedToolchainFingerprint);

/// <summary>Host pool selection recorded in the approved experiment plan.</summary>
public sealed record ExperimentPlacement(string Name, int MinProcessors, ExperimentHostResources Resources);
