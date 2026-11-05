namespace CMakeWorkflow.Client;

/// <summary>Typed read and analysis endpoints over a Host-owned history database.</summary>
/// <remarks>All database and run paths are resolved on the initialized Host workspace.</remarks>
public sealed class HistoryService(CMakeWorkflowClient client)
{
    public Task<ImportHistoryResult> ImportAsync(ImportHistoryOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("history/import");
        return client.Proxy.HistoryImportAsync(options, ct);
    }

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

    public Task<HistoryDetail> ShowAsync(HistoryShowQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/show");
        return client.Proxy.HistoryShowAsync(query, ct);
    }

    public Task<List<HistoryMeasurement>> MetricsAsync(HistoryMetricsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/metrics");
        return client.Proxy.HistoryMetricsAsync(query, ct);
    }

    public Task<List<HistoryMeasurement>> QueryAsync(HistoryMetricsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/query");
        return client.Proxy.HistoryQueryAsync(query, ct);
    }

    public Task<List<HistoryDifference>> CompareAsync(HistoryCompareQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/compare");
        return client.Proxy.HistoryCompareAsync(query, ct);
    }

    public Task<HistoryDetectionReport> DetectAsync(HistoryDetectQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/detect");
        return client.Proxy.HistoryDetectAsync(query, ct);
    }
}
