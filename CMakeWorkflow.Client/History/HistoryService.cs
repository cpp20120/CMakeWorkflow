namespace CMakeWorkflow.Client;

public sealed class HistoryService(CMakeWorkflowClient client)
{
    public Task<ImportHistoryResult> ImportAsync(ImportHistoryOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("history/import");
        return client.Proxy.HistoryImportAsync(options, ct);
    }
}