using System.Text.Json;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    public Task<JsonElement> ExecutionReportAsync(OperationGetRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => ProcessOperationReportReader.ReadAsync(paths,
            request.OperationId, ct), cancellationToken);
}
