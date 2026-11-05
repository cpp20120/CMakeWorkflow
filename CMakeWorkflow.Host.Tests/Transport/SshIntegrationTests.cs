using CMakeWorkflow.Client;
using StreamJsonRpc;

internal static class SshIntegrationTests
{
    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Persistent SSH fixture requires the POSIX test shell.");
            return;
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        await using TransportTestHost host = new();
        await host.ReadyAsync(ct);
        using SshTestPrograms files = new();
        // OpenSSH stand-in executes its remote command through a real POSIX
        // shell. The remote executable is the actual bridge to a persistent Host.
        string remote = files.Script("remote-host", $"""
            test "$1" = 'host'
            test "$2" = 'bridge'
            shift 2
            exec dotnet {SshTestPrograms.Quote(typeof(SshIntegrationTests).Assembly.Location)} --bridge-worker "$@"
            """);
        SshHostOptions options = new("localhost-fixture", host.Endpoint, host.Root)
        {
            RemoteExecutable = remote,
            SshExecutable = files.Ssh(),
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        for (int connection = 0; connection < 2; connection++)
        {
            await using (CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectSshAsync(options, ct))
            {
                SshTransportTests.Check(client.Host.ProcessId == Environment.ProcessId, "SSH started a new Host.");
                for (int i = 0; i < 3; i++)
                {
                    _ = await SshTransportTests.RejectAsync<RemoteInvocationException>(
                        () => client.Operations.GetAsync(Guid.NewGuid().ToString("N"), ct));
                }
            }
            files.AssertExited();
        }
        await using CMakeWorkflowClient direct = await CMakeWorkflowClient.ConnectAsync(new(host.Endpoint, host.Root), ct);
        SshTransportTests.Check(direct.Host.ProcessId == Environment.ProcessId, "SSH disposal stopped the persistent Host.");
        Console.WriteLine("SSH integration passed: SDK, shell quoting, actual bridge, persistent Host, typed reads and reconnect.");
    }
}
