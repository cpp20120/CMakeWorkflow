using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    public Task<OperationReference> ExecutionStartAsync(ExecutionStartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new InvalidOperationException("not_initialized");
        }

        if (durableExecutions is null)
        {
            throw new InvalidOperationException("durable_execution_unavailable");
        }

        WorkspaceReadPaths paths = new(workspace);
        // Paths refer to the Host, including when invoked over SSH. Authorization
        // precedes immutable acceptance and survives loss of the RPC response.
        string manifest = paths.Require(request.Manifest);
        string output = paths.Require(request.ResultsDirectory);
        return Task.FromResult(HostOperationComposition.Start(durableExecutions, paths.WorkspaceDirectory,
            request with { Manifest = manifest, ResultsDirectory = output }));
    }
}
