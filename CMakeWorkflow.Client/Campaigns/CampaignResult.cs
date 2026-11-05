#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record CampaignResult(
    int SchemaVersion,
    string Kind,
    string Status,
    int ExitCode,
    string? ArtifactDirectory);
