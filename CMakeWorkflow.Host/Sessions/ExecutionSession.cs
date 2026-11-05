namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    public Task<OperationReference> ExecutionStartAsync(ExecutionStartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null) throw new InvalidOperationException("not_initialized");
        if (durableExecutions is null) throw new InvalidOperationException("durable_execution_unavailable");
        var paths = new WorkspaceReadPaths(workspace);
        // Paths refer to the Host, including when invoked over SSH. Authorization
        // precedes immutable acceptance and survives loss of the RPC response.
        var manifest = paths.Require(request.Manifest);
        var output = paths.Require(request.ResultsDirectory);
        return Task.FromResult(HostOperationComposition.Start(durableExecutions, paths.WorkspaceDirectory,
            request with { Manifest = manifest, ResultsDirectory = output }));
    }
}
