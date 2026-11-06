namespace CMakeWorkflow.Client.History;

/// <summary>Typed reads, analysis and imports over a Host-owned history database.</summary>
/// <remarks>
/// Database and run paths are interpreted on the initialized Host. This facade borrows the client lifetime.
/// Cancellation is request-scoped and RPC failures propagate without automatic reconnect or retry.
/// </remarks>
public sealed class HistoryService(CMakeWorkflowClient client)
{
    /// <summary>Imports Host run evidence into the history database.</summary>
    /// <remarks>
    /// Writes Host history state. Cancellation or loss of reply may follow committed changes; inspect history before retrying.
    /// </remarks>
    public Task<ImportHistoryResult> ImportAsync(ImportHistoryOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("history/import");
        return client.Proxy.HistoryImportAsync(options, ct);
    }

    /// <summary>Reads imported run summaries.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryRun>> RunsAsync(HistoryRunsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/runs");
        return client.Proxy.HistoryRunsAsync(query, ct);
    }

    /// <summary>Browse all imported runs using stable keyset pages; never re-imports data.</summary>
    public Task<HistoryRunPage> RunsPageAsync(HistoryRunsPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/runsPage");
        return client.Proxy.HistoryRunsPageAsync(query, ct);
    }

    /// <summary>Reads one imported run and its detail.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<HistoryDetail> ShowAsync(HistoryShowQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/show");
        return client.Proxy.HistoryShowAsync(query, ct);
    }

    /// <summary>Reads imported measurement evidence.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryMeasurement>> MetricsAsync(HistoryMetricsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/metrics");
        return client.Proxy.HistoryMetricsAsync(query, ct);
    }

    /// <summary>Queries imported measurement evidence using the metrics alias.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryMeasurement>> QueryAsync(HistoryMetricsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/query");
        return client.Proxy.HistoryQueryAsync(query, ct);
    }

    /// <summary>Compares imported measurements.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryDifference>> CompareAsync(HistoryCompareQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/compare");
        return client.Proxy.HistoryCompareAsync(query, ct);
    }

    /// <summary>Computes detections from imported history.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<HistoryDetectionReport> DetectAsync(HistoryDetectQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/detect");
        return client.Proxy.HistoryDetectAsync(query, ct);
    }
}
