namespace CMakeWorkflow.Client.Workflows;

/// <summary>Executable resolved from the initialized Host workspace without building.</summary>
public sealed record TargetResolution(
    int SchemaVersion, string BuildDirectory, string Target, string? Configuration, string Executable);

internal sealed record ContextInspectRequest(string BuildDirectory);
internal sealed record TargetResolveRequest(string BuildDirectory, string Target, string? Configuration);
