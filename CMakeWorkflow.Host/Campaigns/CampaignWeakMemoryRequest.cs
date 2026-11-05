#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record CampaignWeakMemoryRequest(
    string SourceDirectory,
    string RelacyDirectory,
    string? ReportPath = null,
    string Protocol = "all",
    string Compiler = "c++",
    int TimeoutSeconds = 60);
