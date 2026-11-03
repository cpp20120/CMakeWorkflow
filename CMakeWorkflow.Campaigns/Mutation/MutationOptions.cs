namespace CMakeWorkflow.Campaigns;

// Typed domain entry points; no argv parsing, subprocess CLI or transport dependency.
public sealed record MutationOptions(
    string SourceDirectory,
    string Suite = "all",
    string Compiler = "c++",
    string Seed = "496833221358",
    int Cases = 48,
    int Steps = 48,
    int TimeoutSeconds = 30,
    string? ReportPath = null);
