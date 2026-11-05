namespace CMakeWorkflow.Campaigns.FuzzCorpus;

public sealed record FuzzMergeOptions(string RunDirectory, string? OutputDirectory = null, bool Coverage = false);
