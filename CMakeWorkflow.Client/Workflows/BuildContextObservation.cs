namespace CMakeWorkflow.Client.Workflows;

/// <summary>Host observation of a CMake build tree and its File API metadata.</summary>
public sealed record BuildContextObservation(
    string BuildDirectory,
    string State,
    string Freshness,
    string PresetState,
    string? SourceDirectory,
    string? Generator,
    bool? MultiConfig,
    IReadOnlyList<string> Configurations,
    string? IndexFile,
    string? IndexHash,
    string? CMakeExecutable,
    string? CTestExecutable,
    bool HasCodeModel,
    bool HasCacheReply,
    IReadOnlyList<string> Toolchains,
    IReadOnlyList<TargetObservation> Targets,
    IReadOnlyList<string> Diagnostics,
    ConfigureObservation? ManagedConfigure);

/// <summary>A target observed in the CMake File API.</summary>
public sealed record TargetObservation(
    string Name, string Configuration, string Type, string? Executable, string Evidence);

/// <summary>Recorded provenance of a configure performed by CMakeWorkflow.</summary>
public sealed record ConfigureObservation(
    string State,
    DateTimeOffset At,
    string BuildDirectory,
    string SourceDirectory,
    string? Preset,
    string? IndexFile,
    string? IndexHash,
    int? ExitCode,
    string? Executable,
    string? WorkingDirectory,
    IReadOnlyList<string> Arguments);
