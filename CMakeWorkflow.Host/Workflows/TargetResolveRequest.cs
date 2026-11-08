namespace CMakeWorkflow.Host.Workflows;

public sealed record TargetResolveRequest(string BuildDirectory, string Target, string? Configuration = null);
