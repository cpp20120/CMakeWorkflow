#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

public sealed record ProfileReplayOutcome(int SchemaVersion, string RunDirectory, string AnalysisFile);
