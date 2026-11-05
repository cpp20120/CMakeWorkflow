#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

public sealed record HistoryBuildEnvironment(
    string Directory,
    string? Configuration,
    string? Generator,
    string? Compiler,
    string? CompilerId,
    string? CompilerVersion,
    string? Flags,
    string Fingerprint);
