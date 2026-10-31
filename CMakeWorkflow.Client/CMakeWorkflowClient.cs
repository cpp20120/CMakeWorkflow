using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

/// <summary>Client owns the RPC connection, never the server-side workflow operation.</summary>
public sealed class CMakeWorkflowClient : IAsyncDisposable
{
    private readonly Stream transport;
    private readonly JsonRpc rpc;
    private readonly IClientRpc proxy;
    private readonly Process? ownedHost;
    private readonly string? ownedSocketDirectory;
    private bool disposed;

    private CMakeWorkflowClient(Stream stream, JsonRpc connection, IClientRpc target, HostIdentity identity,
        Process? ownedHost, string? ownedSocketDirectory)
    {
        transport = stream; rpc = connection; proxy = target; Host = identity;
        this.ownedHost = ownedHost; this.ownedSocketDirectory = ownedSocketDirectory;
        Workflows = new(this); Operations = new(this);
    }

    public HostIdentity Host { get; }
    public WorkflowService Workflows { get; }
    public OperationService Operations { get; }

    public static async Task<CMakeWorkflowClient> ConnectAsync(ClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var workspace = Path.GetFullPath(options.Workspace);
        var stream = await ConnectTransportAsync(options.Endpoint, ct);
        try { return await InitializeAsync(stream, workspace, null, null, ct); }
        catch { await stream.DisposeAsync(); throw; }
    }

    /// <summary>Starts a private Host. Disposing the client stops only this owned Host, including its active work.</summary>
    public static async Task<CMakeWorkflowClient> LaunchAsync(LaunchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var workspace = Path.GetFullPath(options.Workspace);
        var dir = OperatingSystem.IsWindows() ? null : Path.Combine(Path.GetTempPath(), "cmw-sdk-" + Guid.NewGuid().ToString("N"));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var endpoint = options.Endpoint ?? (OperatingSystem.IsWindows()
            ? @"\\.\pipe\cmakeworkflow-" + Guid.NewGuid().ToString("N")
            : Path.Combine(dir!, "host.sock"));
        var start = new ProcessStartInfo(options.Executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("host"); start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--endpoint"); start.ArgumentList.Add(endpoint);
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException($"Host exited with code {process.ExitCode} before accepting connections.");
                try
                {
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    connect.CancelAfter(TimeSpan.FromMilliseconds(150));
                    var stream = await ConnectTransportAsync(endpoint, connect.Token);
                    try { return await InitializeAsync(stream, workspace, process, dir, timeout.Token); }
                    catch { await stream.DisposeAsync(); throw; }
                }
                catch (Exception e) when (e is IOException or SocketException or TimeoutException or OperationCanceledException)
                {
                    if (timeout.IsCancellationRequested) throw;
                    await Task.Delay(50, timeout.Token);
                }
            }
        }
        catch
        {
            if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
            throw;
        }
    }

    // StreamJsonRpc 2.26 annotates the formatter constructor because its default
    // settings may use reflection. This instance is configured immediately with
    // the generated ClientJson resolver. Keep this suppression local and exercise
    // it in a published NativeAOT consumer before shipping changes to wire DTOs.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Formatter uses ClientJson source-generated metadata; verified by the AOT consumer smoke test.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Formatter uses ClientJson source-generated metadata; verified by the AOT consumer smoke test.")]
    private static async Task<CMakeWorkflowClient> InitializeAsync(Stream stream, string workspace,
        Process? process, string? directory, CancellationToken ct)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new(ClientJson.Default.Options) { TypeInfoResolver = ClientJson.Default }
        };
        var rpc = new ApplicationRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
        // Diagnostic opt-in for NativeAOT RPC smoke. A lost $/cancelRequest is
        // otherwise invisible when StreamJsonRpc dispatches it asynchronously.
        if (Environment.GetEnvironmentVariable("CMAKEWORKFLOW_RPC_TRACE") == "1")
        {
            rpc.TraceSource.Switch.Level = SourceLevels.All;
            rpc.TraceSource.Listeners.Add(new TextWriterTraceListener(Console.Error));
        }
        try
        {
            var proxy = rpc.Attach<IClientRpc>();
            rpc.StartListening();
            var info = await proxy.InitializeAsync(new(1, workspace), ct);
            if (info.ProtocolVersion != 1) throw new InvalidDataException("Unsupported Host protocol version.");
            return new(stream, rpc, proxy, info, process, directory);
        }
        catch { rpc.Dispose(); throw; }
    }

    // Match the Host's ApplicationError wire contract without depending on Host
    // assemblies. Unknown/standard RPC errors retain StreamJsonRpc's error DTO.
    private sealed class ApplicationRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
    {
        protected override Type? GetErrorDetailsDataType(StreamJsonRpc.Protocol.JsonRpcError error) =>
            (int?)error.Error?.Code == 1001 ? typeof(RpcErrorData) : base.GetErrorDetailsDataType(error);
    }

    private static async Task<Stream> ConnectTransportAsync(string endpoint, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            const string prefix = @"\\.\pipe\cmakeworkflow-";
            if (!endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid named pipe endpoint.", nameof(endpoint));
            var pipe = new NamedPipeClientStream(".", endpoint[@"\\.\pipe\".Length..], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(ct); return pipe; }
            catch { pipe.Dispose(); throw; }
        }
        if (!Path.IsPathFullyQualified(endpoint) || Encoding.UTF8.GetByteCount(endpoint) > 100)
            throw new ArgumentException("Unix socket endpoint must be absolute and at most 100 UTF-8 bytes.", nameof(endpoint));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }

    internal IClientRpc Proxy { get { ObjectDisposedException.ThrowIf(disposed, this); return proxy; } }
    internal Task<JsonElement> InvokePlanAsync(PlanRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return rpc.InvokeWithCancellationAsync<JsonElement>("workflow/plan", new object?[] { request }, ct);
    }
    internal void RequireCapability(string capability)
    {
        if (!Host.Capabilities.Contains(capability, StringComparer.Ordinal))
            throw new NotSupportedException($"Host does not advertise {capability}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        rpc.Dispose(); await transport.DisposeAsync();
        if (ownedHost is not null)
        {
            try { if (!ownedHost.HasExited) { ownedHost.Kill(entireProcessTree: true); await ownedHost.WaitForExitAsync(); } }
            finally { ownedHost.Dispose(); }
        }
        if (ownedSocketDirectory is not null && Directory.Exists(ownedSocketDirectory))
            Directory.Delete(ownedSocketDirectory, recursive: true);
    }
}

public sealed class WorkflowService(CMakeWorkflowClient client)
{
    public async Task<WorkflowOperation> StartAsync(StartWorkflowOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("workflow/start");
        if (string.IsNullOrWhiteSpace(options.ClientRequestId)) throw new ArgumentException("ClientRequestId is required.");
        var result = await client.Proxy.StartAsync(options, ct);
        return new WorkflowOperation(client, result.OperationId);
    }

    public Task<JsonElement> PlanAsync(string manifest, CancellationToken ct = default)
    {
        client.RequireCapability("workflow/plan");
        // InvokeWithCancellationAsync explicitly binds ct as the RPC invocation
        // cancellation token, never as a serialized member of the params array.
        return client.InvokePlanAsync(new PlanRequest(manifest), ct);
    }
}

public sealed class OperationService(CMakeWorkflowClient client)
{
    public WorkflowOperation GetHandle(string operationId) => new(client, operationId);
    public Task<OperationSnapshot> GetAsync(string operationId, CancellationToken ct = default) =>
        client.Proxy.GetAsync(new(operationId), ct);
    public Task<OperationSnapshot> FindAsync(string clientRequestId, CancellationToken ct = default) =>
        client.Proxy.FindAsync(new(clientRequestId), ct);
}

public sealed class WorkflowOperation(CMakeWorkflowClient client, string operationId)
{
    public string OperationId { get; } = operationId;
    public Task<OperationSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        client.Operations.GetAsync(OperationId, ct);
    public Task<CancellationReceipt> RequestCancellationAsync(long expectedRevision, CancellationToken ct = default) =>
        client.Proxy.CancelAsync(new(OperationId, expectedRevision), ct);
    public Task<RecoverySnapshot> InspectRecoveryAsync(CancellationToken ct = default) =>
        client.Proxy.InspectAsync(new(OperationId), ct);
    public Task<RecoverySnapshot> ReconcileAsync(long revision, string decision, string note,
        bool confirmedQuiescent, CancellationToken ct = default) =>
        client.Proxy.ReconcileAsync(new(OperationId, revision, decision, note, confirmedQuiescent), ct);
    public Task<RecoverySnapshot> AdmitResumeAsync(long revision, bool confirmedQuiescent, CancellationToken ct = default) =>
        client.Proxy.AdmitResumeAsync(new(OperationId, revision, confirmedQuiescent), ct);
    public Task<RecoverySnapshot> ExecuteResumeAsync(long revision = 2, CancellationToken ct = default) =>
        client.Proxy.ExecuteResumeAsync(new(OperationId, revision, true), ct);
    public Task<EventPage> ReadEventsAsync(long afterSequence = 0, int limit = 100, CancellationToken ct = default) =>
        client.Proxy.EventsAsync(new(OperationId, afterSequence, limit), ct);

    public async IAsyncEnumerable<JsonElement> WatchEventsAsync(long afterSequence = 0,
        TimeSpan? pollInterval = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = afterSequence;
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        while (true)
        {
            var page = await ReadEventsAsync(cursor, 100, ct);
            foreach (var evt in page.Events) yield return evt;
            cursor = page.NextSequence;
            if (page.Events.Count == 0) await Task.Delay(delay, ct);
        }
    }

    public async Task<OperationSnapshot> WaitForCompletionAsync(TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        while (true)
        {
            var state = await GetSnapshotAsync(ct);
            if (state.State is "succeeded" or "failed" or "canceled") return state;
            await Task.Delay(delay, ct);
        }
    }
}
