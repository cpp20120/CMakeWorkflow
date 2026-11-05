using CMakeWorkflow.Client;
using StreamJsonRpc;

internal static class SshTransportTests
{
    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SSH process fixture requires a POSIX shell; Windows client execution is not validated here.");
            return;
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(50));
        CancellationToken ct = deadline.Token;
        using SshTestPrograms files = new();
        SshHostOptions options = new("configured-alias", "/remote/host ' ;$(exit 44).sock", files.Root)
        {
            RemoteExecutable = files.Peer(),
            SshExecutable = files.Ssh(),
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        await using (CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectSshAsync(options, ct))
        {
            Check(client.Host.WorkspaceDirectory == files.Root, "Remote workspace changed.");
            Check(client.Host.Capabilities.Contains("workflow/start"), "Capabilities were lost.");
            for (int i = 0; i < 3; i++)
            {
                ToolchainInventory inventory = await client.Toolchains.DiscoverAsync(ct);
                Check(inventory.SchemaVersion == 1, "Typed read result was corrupted.");
                _ = await RejectAsync<RemoteInvocationException>(
                    () => client.Operations.GetAsync(Guid.NewGuid().ToString("N"), ct));
            }
            Check(client.SshDiagnostics.Length <= 16 * 1024 && client.SshDiagnostics.Contains("SSH_DIAGNOSTICS_TAIL", StringComparison.Ordinal),
                "SSH stderr was unbounded or mixed with RPC stdout.");
        }
        files.AssertExited();
        Check((await File.ReadAllLinesAsync(files.Arguments, CancellationToken.None))[4] == "configured-alias", "SSH alias changed.");
        Console.WriteLine("SSH: quoting, alias, handshake, sequential typed reads, bounded stderr and disposal passed.");

        foreach (string? bad in new[] { "relative", "C:\\local", "/remote/../ambiguous" })
        {
            _ = await RejectAsync<ArgumentException>(() => CMakeWorkflowClient.ConnectSshAsync(options with { Workspace = bad }, ct));
        }

        _ = await RejectAsync<ArgumentException>(() => CMakeWorkflowClient.ConnectSshAsync(options with { Destination = "-oProxyCommand=bad" }, ct));
        _ = await RejectAsync<SshConnectionException>(() => CMakeWorkflowClient.ConnectSshAsync(options with { SshExecutable = Path.Combine(files.Root, "missing-ssh") }, ct));
        _ = await RejectAsync<SshConnectionException>(() => CMakeWorkflowClient.ConnectSshAsync(options with { RemoteExecutable = Path.Combine(files.Root, "missing-host") }, ct));
        files.AssertExited();

        string exited = files.Script("exit-ssh", "echo 'SSH_EXIT_DIAGNOSTIC' >&2\nexit 23");
        SshConnectionException failure = await RejectAsync<SshConnectionException>(() => CMakeWorkflowClient.ConnectSshAsync(options with { SshExecutable = exited }, ct));
        Check(failure.Diagnostics.Contains("SSH_EXIT_DIAGNOSTIC", StringComparison.Ordinal) && failure.ExitCode == 23, "Early SSH exit diagnostics lost.");
        foreach (string? payload in new[] { "not rpc", "Content-Length: 100\r\n\r\n{}" })
        {
            string broken = files.Script("broken-ssh", "printf '%s' " + SshTestPrograms.Quote(payload));
            _ = await RejectAsync<Exception>(() => CMakeWorkflowClient.ConnectSshAsync(options with { SshExecutable = broken }, ct));
        }
        string mismatch = files.Script("mismatch-ssh", "exec dotnet " + SshTestPrograms.Quote(typeof(SshTransportTests).Assembly.Location) + " --ssh-mismatch-peer");
        Exception protocol = await RejectAsync<Exception>(() => CMakeWorkflowClient.ConnectSshAsync(options with { SshExecutable = mismatch }, ct));
        Check(protocol.ToString().Contains("Unsupported Host protocol", StringComparison.Ordinal), "Protocol mismatch not detected.");

        await SshLifecycleTests.RunAsync();
        Console.WriteLine("SSH: invalid paths, missing executables, early exit, malformed/truncated framing, protocol mismatch, timeout and cancellation passed.");
    }

    internal static async Task<T> RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new System.Diagnostics.UnreachableException("Expected " + typeof(T).Name);
    }

    internal static void Check(bool success, string message)
    {
        if (!success)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }
}
