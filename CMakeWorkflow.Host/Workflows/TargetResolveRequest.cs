// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record TargetResolveRequest(string BuildDirectory, string Target, string? Configuration = null);
