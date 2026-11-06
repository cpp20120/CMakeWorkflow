using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Tooling.Tools;
using StreamJsonRpc;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Transport;
#pragma warning restore IDE0130

/// <summary>
/// Private, single-client Host over a binary stdio pair. The session owns the
/// coordinator: losing the channel cancels in-flight work and exits the Host.
/// It never binds a socket or joins a persistent Host's operation registry.
/// </summary>
internal static class StdioHostServer
{
    /// <summary>Serves one private RPC channel and drains its owned coordinator on channel loss.</summary>
    /// <remarks>
    /// Owns the connection handler, tooling resources and private durable coordinator for this invocation.
    /// Disconnect or caller cancellation revokes the shared private lifetime, then session/coordinator drain runs
    /// before normal return. No persistent Host coordinator is shared, so accepted work cannot keep executing here
    /// after normal shutdown. Drain exceptions can propagate; shutdown does not erase ledger/recovery evidence.
    /// </remarks>
    /// <param name="input">Binary RPC input; handler disposal closes its stream.</param>
    /// <param name="output">Binary RPC output; diagnostic text must use a separate channel.</param>
    /// <param name="ct">Private Host lifetime, linked to transport disconnect.</param>
    internal static async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using FixCoordinator fixes = new();
        using DurableExecutionCoordinator executions = new(lifetime.Token);
        using HttpClient downloads = VerifiedCMakeInstaller.CreateDownloadClient();
        VerifiedCMakeInstaller installer = new(downloads);
        using JsonRpc rpc = HostConnection.Create(input, output);
        DoctorSession session = new(
            workspace => new ToolingService(new SystemToolingRuntime(workspace), installer),
            value => rpc.NotifyAsync("tooling/progress", value), lifetime.Token,
            fixes, value => rpc.NotifyAsync("tooling/fixProgress", value), durableExecutions: executions);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<Protocol.IHostRpc>(), session, null);
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
