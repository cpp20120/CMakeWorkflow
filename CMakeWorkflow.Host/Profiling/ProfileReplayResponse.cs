namespace CMakeWorkflow.Host.Profiling;

public sealed record ProfileReplayResponse(int SchemaVersion, string RunDirectory, string AnalysisFile);
