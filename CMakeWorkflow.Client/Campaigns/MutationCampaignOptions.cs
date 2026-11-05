namespace CMakeWorkflow.Client.Campaigns;

public sealed record MutationCampaignOptions(
    string SourceDirectory,
    string? ReportPath = null,
    string Suite = "all",
    string Compiler = "c++",
    string Seed = "496833221358",
    int Cases = 48,
    int Steps = 48,
    int TimeoutSeconds = 30);
