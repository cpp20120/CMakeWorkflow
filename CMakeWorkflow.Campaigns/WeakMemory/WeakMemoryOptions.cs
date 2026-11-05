#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

public sealed record WeakMemoryOptions(
    string SourceDirectory,
    string Protocol,
    string RelacyDirectory,
    string Compiler = "c++",
    int TimeoutSeconds = 60,
    string? ReportPath = null,
    string? Mutant = null);
