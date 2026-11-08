using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Tooling.Tools;
using StreamJsonRpc;

// Test process using the real generated Host protocol over redirected stdio.
// It tests SSH client plumbing without requiring sockets, sshd or credentials.
namespace CMakeWorkflow.Host.Tests.Transport;

internal static class SshTestPeer
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 5 || !args.AsSpan()[..4].SequenceEqual(["host", "bridge", "--stdio", "--endpoint"]))
        {
            throw new System.Diagnostics.UnreachableException("Remote shell changed command arguments.");
        }

        if (args[4] != "/remote/host ' ;$(exit 44).sock")
        {
            throw new System.Diagnostics.UnreachableException("Remote shell expanded the endpoint.");
        }

        await Console.Error.WriteAsync(new string('x', 64 * 1024));
        await Console.Error.WriteAsync("SSH_DIAGNOSTICS_TAIL");
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        using FixCoordinator fixes = new();
        using HttpClient downloads = VerifiedCMakeInstaller.CreateDownloadClient();
        using DurableExecutionCoordinator executions = new(stop.Token);
        using JsonRpc rpc = HostConnection.Create(Console.OpenStandardInput(), Console.OpenStandardOutput());
        using DoctorSession session = new(
            workspace => new ToolingService(new SystemToolingRuntime(workspace), new VerifiedCMakeInstaller(downloads)),
            _ => Task.CompletedTask, stop.Token, fixes, _ => Task.CompletedTask, durableExecutions: executions);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<Protocol.IHostRpc>(), session, null);
        await using CancellationTokenRegistration canceled = stop.Token.Register(rpc.Dispose);
        rpc.StartListening();
        try { await rpc.Completion; }
        finally
        {
            await stop.CancelAsync();
            await session.DrainAsync();
            await executions.DrainAsync();
        }
    }
}