namespace CMakeWorkflow.Client.Campaigns;

public sealed record WeakMemoryCampaignOptions(
    string SourceDirectory,
    string RelacyDirectory,
    string? ReportPath = null,
    string Protocol = "all",
    string Compiler = "c++",
    int TimeoutSeconds = 60);
