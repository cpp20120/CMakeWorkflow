using System.Runtime.CompilerServices;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.Client.History;

/// <summary>Typed reads, analysis and imports over a Host-owned history database.</summary>
/// <remarks>
/// Database and run paths are interpreted on the initialized Host. This facade borrows the client lifetime.
/// Cancellation is request-scoped and RPC failures propagate without automatic reconnect or retry.
/// </remarks>
public sealed class HistoryService(CMakeWorkflowClient client)
{
    /// <summary>Reads indexing state independently of the workflow terminal outcome.</summary>
    public Task<HistoryIndexStatus> GetIndexStatusAsync(string operationId, CancellationToken ct = default)
    {
        client.RequireCapability("history/indexStatus");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryIndexStatusAsync(new(operationId), ct));
    }

    /// <summary>Attempts indexing for a finalized operation without dispatching workflow nodes.</summary>
    public Task<HistoryIndexStatus> RetryIndexAsync(string operationId, CancellationToken ct = default)
    {
        client.RequireCapability("history/retryIndex");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryRetryIndexAsync(new(operationId), ct));
    }

    /// <summary>Polls independent indexing state until disabled, indexed or failed. Cancel or apply a token deadline to stop waiting.</summary>
    public async Task<HistoryIndexStatus> WaitForIndexAsync(string operationId, TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        TimeSpan delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            HistoryIndexStatus status = await GetIndexStatusAsync(operationId, ct);
            if (status.State is HistoryIndexState.Disabled or HistoryIndexState.Indexed or HistoryIndexState.Failed)
            {
                return status;
            }
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Reads committed run identities for an indexed operation.</summary>
    public Task<OperationHistoryRuns> GetOperationRunsAsync(string operationId, CancellationToken ct = default)
    {
        client.RequireCapability("history/operationRuns");
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryOperationRunsAsync(new(operationId), ct));
    }

    /// <summary>Returns bounded, complete measurements from the indexed runs of one operation.</summary>
    /// <remarks>These reads are not a database snapshot. A changed import receipt causes rejection.</remarks>
    public async Task<IReadOnlyList<HistoryMeasurement>> QueryOperationAsync(string operationId,
        string? metric = null, string? variant = null, string? scenario = null, string? environment = null,
        CancellationToken ct = default)
    {
        const int limit = 50000;
        OperationHistoryRuns receipt = await GetOperationRunsAsync(operationId, ct);
        List<HistoryMeasurement> measurements = [];
        foreach (IndexedRunIdentity identity in receipt.Runs)
        {
            List<HistoryMeasurement> rows = await MetricsAsync(
                new(receipt.DatabasePath, identity.RunId, metric, variant, scenario) { Environment = environment }, ct);
            if (rows.Count > limit - measurements.Count)
            {
                throw new InvalidDataException($"Operation measurement limit {limit} exceeded; narrow the query.");
            }
            measurements.AddRange(rows);
        }
        OperationHistoryRuns current = await GetOperationRunsAsync(operationId, ct);
        return current.DatabasePath == receipt.DatabasePath && current.Runs.SequenceEqual(receipt.Runs)
            ? measurements.AsReadOnly()
            : throw new InvalidDataException("The indexed run identities changed during the measurement query.");
    }

    /// <summary>Imports Host run evidence into the history database.</summary>
    /// <remarks>
    /// Writes Host history state. Cancellation or loss of reply may follow committed changes; inspect history before retrying.
    /// </remarks>
    public Task<ImportHistoryResult> ImportAsync(ImportHistoryOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("history/import");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryImportAsync(options, ct));
    }

    /// <summary>Reads imported run summaries.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryRun>> RunsAsync(HistoryRunsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/runs");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryRunsAsync(query, ct));
    }

    /// <summary>Browse all imported runs using stable keyset pages; never re-imports data.</summary>
    public Task<HistoryRunPage> RunsPageAsync(HistoryRunsPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/runsPage");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryRunsPageAsync(query, ct));
    }

    /// <summary>Enumerates at most the requested number of keyset pages. Cursor and filters are forwarded unchanged.</summary>
    public async IAsyncEnumerable<HistoryRunPage> EnumerateRunPagesAsync(HistoryRunsPageQuery query, int maxPages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        for (int page = 0; page < maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            HistoryRunPage result = await RunsPageAsync(query, ct);
            string? next = result.NextCursor is { } cursor && cursor == query.Cursor
                ? throw new InvalidDataException("The Host returned a non-progressing History cursor.")
                : result.NextCursor;

            yield return result;
            if (next is null)
            {
                yield break;
            }
            query = query with { Cursor = next };
        }
    }

    /// <summary>Reads one imported run and its detail.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<HistoryDetail> ShowAsync(HistoryShowQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/show");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryShowAsync(query, ct));
    }

    /// <summary>Reads imported measurement evidence.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryMeasurement>> MetricsAsync(HistoryMetricsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireEnvironmentFilter(query);
        client.RequireCapability("history/metrics");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryMetricsAsync(query, ct));
    }

    /// <summary>Queries imported measurement evidence using the metrics alias.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryMeasurement>> QueryAsync(HistoryMetricsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireEnvironmentFilter(query);
        client.RequireCapability("history/query");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryQueryAsync(query, ct));
    }

    /// <summary>Compares imported measurements.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<List<HistoryDifference>> CompareAsync(HistoryCompareQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Selection is not null)
        {
            client.RequireCapability("history/selectedCompare");
        }
        client.RequireCapability("history/compare");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryCompareAsync(query, ct));
    }

    /// <summary>Computes detections from imported history.</summary>
    /// <remarks>
    /// Does not start or cancel workflow execution; cancellation affects only this query.
    /// </remarks>
    public Task<HistoryDetectionReport> DetectAsync(HistoryDetectQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        client.RequireCapability("history/detect");
        return RpcRefusals.TranslateAsync(() => client.Proxy.HistoryDetectAsync(query, ct));
    }

    private void RequireEnvironmentFilter(HistoryMetricsQuery query)
    {
        if (query.Environment is not null)
        {
            client.RequireCapability("history/environmentFilter");
        }
    }
}
