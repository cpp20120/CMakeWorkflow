using System.Diagnostics;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Transport;

internal static class SshLifecycleTests
{
    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using SshTestPrograms files = new();
        string sleeping = files.Script("sleeping-ssh", "printf '%s' \"$$\" > " + SshTestPrograms.Quote(files.PidFile) + "\nexec sleep 30");
        SshHostOptions options = new("alias", "/remote/host.sock", files.Root)
        {
            SshExecutable = sleeping,
            ConnectionTimeout = TimeSpan.FromMilliseconds(300)
        };
        Stopwatch watch = Stopwatch.StartNew();
        _ = await SshTransportTests.RejectAsync<TimeoutException>(() => CMakeWorkflowClient.ConnectSshAsync(options));
        SshTransportTests.Check(watch.Elapsed < TimeSpan.FromSeconds(3), "Connection timeout took " + watch.Elapsed);
        files.AssertExited();
        Console.WriteLine($"SSH timeout including cleanup: {watch.Elapsed.TotalMilliseconds:F0}ms.");
        watch.Restart();
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));
        _ = await SshTransportTests.RejectAsync<OperationCanceledException>(() => CMakeWorkflowClient.ConnectSshAsync(
            options with { ConnectionTimeout = TimeSpan.FromSeconds(10) }, cancel.Token));
        SshTransportTests.Check(watch.Elapsed < TimeSpan.FromSeconds(3), "Cancellation took " + watch.Elapsed);
        files.AssertExited();
        Console.WriteLine($"SSH cancellation including cleanup: {watch.Elapsed.TotalMilliseconds:F0}ms.");

        // The descendant keeps both output pipes open. Teardown must terminate
        // the entire owned process tree before waiting for pipe EOF.
        string childPid = Path.Combine(files.Root, "child-pid");
        string tree = files.Script("ssh-with-child", $"""
            printf '%s' "$$" > {SshTestPrograms.Quote(files.PidFile)}
            sleep 30 &
            printf '%s' "$!" > {SshTestPrograms.Quote(childPid)}
            wait
            """);
        watch.Restart();
        _ = await SshTransportTests.RejectAsync<TimeoutException>(() => CMakeWorkflowClient.ConnectSshAsync(
            options with { SshExecutable = tree }));
        SshTransportTests.Check(watch.Elapsed < TimeSpan.FromSeconds(3), "Process-tree cleanup took " + watch.Elapsed);
        files.AssertExited();
        files.AssertExited(childPid);
        Console.WriteLine($"SSH process-tree cleanup including timeout: {watch.Elapsed.TotalMilliseconds:F0}ms.");
        Console.WriteLine("SSH lifecycle passed: 300ms timeout/cancellation finish within 3s and reap the process.");
    }
}
