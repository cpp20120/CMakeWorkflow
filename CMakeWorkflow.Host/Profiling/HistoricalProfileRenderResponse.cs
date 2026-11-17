namespace CMakeWorkflow.Host.Profiling;

public sealed record HistoricalProfileRenderResponse(int SchemaVersion, string RunDirectory, string IndexFile,
    string SamplingSummaryFile, string FramePointerSummaryFile, int FlamegraphCount);
