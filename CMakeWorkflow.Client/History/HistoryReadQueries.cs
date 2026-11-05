namespace CMakeWorkflow.Client.History;

// Paths belong to the initialized Host workspace (also for SSH clients).
public sealed record HistoryRunsQuery(string DatabasePath, int Limit = 50, string? Status = null,
    string? Since = null, string? Until = null, string? Workflow = null, string? Variant = null);

/// <summary>Browse historical runs newest-first with an opaque workspace-bound cursor.</summary>
/// <remarks>Importing new runs does not insert them into an existing traversal.
/// Reimports and deletions are not snapshot-isolated.</remarks>
public sealed record HistoryRunsPageQuery(string DatabasePath, int Limit = 50,
    string? Status = null, string? Since = null, string? Until = null,
    string? Workflow = null, string? Variant = null, string? Cursor = null);

public sealed record HistoryShowQuery(string DatabasePath, string RunId);

public sealed record HistoryMetricsQuery(string DatabasePath, string? RunId = null, string? Metric = null,
    string? Variant = null, string? Scenario = null);

public sealed record HistoryCompareQuery(string DatabasePath, string BaselineRunId, string CandidateRunId);

public sealed record HistoryDetectQuery(string DatabasePath, string BaselineRunId, string CandidateRunId,
    string Metric, string Direction, double MinimumEffectPercent,
    string? Variant = null, string? Scenario = null, double Alpha = 0.05);
