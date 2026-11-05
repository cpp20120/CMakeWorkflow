using CMakeWorkflow.Client;
using CMakeWorkflow.Host;

internal sealed class TransportTestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(90));
    private readonly Task server;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cmwr-" + Guid.NewGuid().ToString("N")[..10]);
    public string Endpoint { get; }

    public TransportTestHost()
    {
        _ = Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Endpoint = OperatingSystem.IsWindows()
            ? @"\\.\pipe\cmakeworkflow-" + Guid.NewGuid().ToString("N")
            : Path.Combine(Root, "host.sock");
        server = HostServer.RunAsync(Endpoint, lifetime.Token);
    }

    public async Task ReadyAsync(CancellationToken ct)
    {
        while (true)
        {
            if (server.IsCompleted)
            {
                await server;
            }

            try
            {
                await using CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(new(Endpoint, Root), ct);
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(20, ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try { await server.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            lifetime.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
