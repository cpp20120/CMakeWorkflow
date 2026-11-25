using System.Diagnostics;
using System.Net.Sockets;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Host.Transport;

namespace CMakeWorkflow.Developer;

/// <summary>Owns an in-process local Host and a Client SDK connection. Durable evidence remains in the workspace.</summary>
public sealed class LocalDeveloperHost : IAsyncDisposable
{
    private readonly DirectoryInfo transportDirectory;
    private readonly CancellationTokenSource lifetime;
    private readonly Task listener;

    private LocalDeveloperHost(DirectoryInfo transportDirectory, CancellationTokenSource lifetime, Task listener, CMakeWorkflowClient client)
    {
        this.transportDirectory = transportDirectory;
        this.lifetime = lifetime;
        this.listener = listener;
        Client = client;
    }

    public CMakeWorkflowClient Client { get; }

    public static async Task<LocalDeveloperHost> OpenAsync(string workspace, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(workspace);
        DirectoryInfo transportDirectory = Directory.CreateTempSubdirectory("cmwf-dev-");
        CancellationTokenSource lifetime = new();
        string endpoint = OperatingSystem.IsWindows()
            ? @"\\.\pipe\cmakeworkflow-" + Guid.NewGuid().ToString("N")
            : Path.Combine(transportDirectory.FullName, "host.sock");
        Task listener = HostServer.RunAsync(endpoint, lifetime.Token);
        try
        {
            while (!listener.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(new(endpoint, root), ct);
                    return new(transportDirectory, lifetime, listener, client);
                }
                catch (SocketException)
                {
                    await Task.Delay(20, ct);
                }
            }
            await listener;
            throw new IOException("The local Host stopped before initialization.");
        }
        catch
        {
            await ShutdownAsync(transportDirectory, lifetime, listener);
            throw;
        }
    }

    /// <summary>Waits for a finalized report or the end of scheduling by this local Host.</summary>
    /// <remarks>An inactive unfinalized operation retains its durable recovery state.</remarks>
    public async Task<OperationOverview> WaitForOperationAsync(string operationId, CancellationToken ct = default)
    {
        while (true)
        {
            OperationOverview overview = await Client.Operations.GetOverviewAsync(operationId, ct);
            if (overview.IsFinalized || overview.IsScheduledOnHost == false)
            {
                return overview;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }
    }

    /// <summary>Observes Host indexing until it settles or the requested observation interval elapses.</summary>
    public async Task<HistoryIndexStatus> ObserveHistoryAsync(string operationId, TimeSpan observationInterval,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(observationInterval, TimeSpan.Zero);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            HistoryIndexStatus status = await Client.History.GetIndexStatusAsync(operationId, ct);
            if (status.State is not (HistoryIndexState.Pending or HistoryIndexState.Indexing) ||
                Stopwatch.GetElapsedTime(started) >= observationInterval)
            {
                return status;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        finally
        {
            await ShutdownAsync(transportDirectory, lifetime, listener);
        }
    }

    private static async Task ShutdownAsync(DirectoryInfo directory, CancellationTokenSource lifetime, Task listener)
    {
        try
        {
            await lifetime.CancelAsync();
            await listener;
        }
        finally
        {
            lifetime.Dispose();
            directory.Delete(recursive: true);
        }
    }
}
