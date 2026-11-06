// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Campaigns;
#pragma warning restore IDE0130

public sealed record CampaignWeakMemoryRequest(
    string SourceDirectory,
    string RelacyDirectory,
    string? ReportPath = null,
    string Protocol = "all",
    string Compiler = "c++",
    int TimeoutSeconds = 60);
