namespace CMakeWorkflow.Client.Workflows;

internal sealed record PlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);
