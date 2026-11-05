namespace CMakeWorkflow.Campaigns.WeakMemory;

public sealed record WeakMemoryOptions(
    string SourceDirectory,
    string Protocol,
    string RelacyDirectory,
    string Compiler = "c++",
    int TimeoutSeconds = 60,
    string? ReportPath = null,
    string? Mutant = null);
