#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

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
