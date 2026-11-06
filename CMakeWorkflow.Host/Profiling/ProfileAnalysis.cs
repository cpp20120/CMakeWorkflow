// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Profiling;
#pragma warning restore IDE0130

public sealed record ProfileAnalysis(int SchemaVersion, string RunDirectory, string AnalysisFile);
