#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record MutationCampaignOptions(
    string SourceDirectory,
    string? ReportPath = null,
    string Suite = "all",
    string Compiler = "c++",
    string Seed = "496833221358",
    int Cases = 48,
    int Steps = 48,
    int TimeoutSeconds = 30);
