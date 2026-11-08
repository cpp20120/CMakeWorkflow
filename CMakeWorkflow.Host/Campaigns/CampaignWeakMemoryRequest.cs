namespace CMakeWorkflow.Host.Campaigns;

public sealed record CampaignWeakMemoryRequest(
    string SourceDirectory,
    string RelacyDirectory,
    string? ReportPath = null,
    string Protocol = "all",
    string Compiler = "c++",
    int TimeoutSeconds = 60);
