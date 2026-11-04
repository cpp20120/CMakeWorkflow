using CMakeWorkflow.Host;
using CMakeWorkflow.Tooling;
using StreamJsonRpc;

// Test process using the real generated Host protocol over redirected stdio.
// It tests SSH client plumbing without requiring sockets, sshd or credentials.
internal static class SshTestPeer
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 5 || !args[..4].SequenceEqual(new[] { "host", "bridge", "--stdio", "--endpoint" }))
            throw new Exception("Remote shell changed command arguments.");
        if (args[4] != "/remote/host ' ;$(exit 44).sock")
            throw new Exception("Remote shell expanded the endpoint.");
        Console.Error.Write(new string('x', 64 * 1024));
        Console.Error.Write("SSH_DIAGNOSTICS_TAIL");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixes = new FixCoordinator();
        using var downloads = VerifiedCMakeInstaller.CreateDownloadClient();
        using var executions = new DurableExecutionCoordinator(stop.Token);
        using var rpc = HostConnection.Create(Console.OpenStandardInput(), Console.OpenStandardOutput());
        var session = new DoctorSession(
            workspace => new ToolingService(new SystemToolingRuntime(workspace), new VerifiedCMakeInstaller(downloads)),
            _ => Task.CompletedTask, stop.Token, fixes, _ => Task.CompletedTask, durableExecutions: executions);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IHostRpc>(), session, null);
        using var canceled = stop.Token.Register(rpc.Dispose);
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
