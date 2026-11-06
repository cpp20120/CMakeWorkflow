// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed record ToolingExecutionProgress(string Tool, string Stage, string? Message);
