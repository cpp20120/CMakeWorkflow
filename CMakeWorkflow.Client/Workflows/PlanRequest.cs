#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

internal sealed record PlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);
