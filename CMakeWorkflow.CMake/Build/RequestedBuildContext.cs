#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record RequestedBuildContext(
    string BuildDirectory,
    string? Configuration = null,
    string? ConfigurePreset = null,
    string? SourceDirectory = null,
    string ProvenancePolicy = "strict",
    string Mode = "legacy",
    string? Generator = null);
