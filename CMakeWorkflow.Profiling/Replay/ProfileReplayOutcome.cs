namespace CMakeWorkflow.Profiling.Replay;

public sealed record ProfileReplayOutcome(int SchemaVersion, string RunDirectory, string AnalysisFile);
