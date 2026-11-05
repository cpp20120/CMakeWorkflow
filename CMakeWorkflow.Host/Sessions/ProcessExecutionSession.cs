using System.Text.Json;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    public Task<JsonElement> ExecutionReportAsync(OperationGetRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => ProcessOperationReportReader.ReadAsync(paths,
            request.OperationId, ct), cancellationToken);
}
