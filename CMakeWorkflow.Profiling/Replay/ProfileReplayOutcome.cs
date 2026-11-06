// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Replay;
#pragma warning restore IDE0130

public sealed record ProfileReplayOutcome(int SchemaVersion, string RunDirectory, string AnalysisFile);
