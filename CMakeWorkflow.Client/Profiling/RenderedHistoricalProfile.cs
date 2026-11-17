namespace CMakeWorkflow.Client.Profiling;

/// <summary>Host paths and flamegraph count for a rendered historical campaign.</summary>
public sealed record RenderedHistoricalProfile(int SchemaVersion, string RunDirectory, string IndexFile,
    string SamplingSummaryFile, string FramePointerSummaryFile, int FlamegraphCount);
