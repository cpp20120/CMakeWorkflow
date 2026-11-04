using CMakeWorkflow.Tooling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

/// <summary>
/// Private, single-client Host over a binary stdio pair. The session owns the
/// coordinator: losing the channel cancels in-flight work and exits the Host.
/// It never binds a socket or joins a persistent Host's operation registry.
/// </summary>
internal static class StdioHostServer
{
    internal static async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var fixes = new FixCoordinator();
        using var executions = new DurableExecutionCoordinator(lifetime.Token);
        using var downloads = VerifiedCMakeInstaller.CreateDownloadClient();
        var installer = new VerifiedCMakeInstaller(downloads);
        using var rpc = HostConnection.Create(input, output);
        var session = new DoctorSession(
            workspace => new ToolingService(new SystemToolingRuntime(workspace), installer),
            value => rpc.NotifyAsync("tooling/progress", value), lifetime.Token,
            fixes, value => rpc.NotifyAsync("tooling/fixProgress", value), durableExecutions: executions);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IHostRpc>(), session, null);
        // On channel loss, revoke the lifetime token owned by this *private*
        // coordinator; persistent Host sessions deliberately do NOT do this.
        rpc.Disconnected += (_, _) => lifetime.Cancel();
        rpc.StartListening();
        try
        {
            await rpc.Completion.WaitAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Client hangup or caller cancellation: drain below.
        }
        finally
        {
            await lifetime.CancelAsync();
            await session.DrainAsync();
            await executions.DrainAsync();
        }
    }
}
