using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

/// <summary>Client owns the RPC connection, never the server-side workflow operation.</summary>
public sealed partial class CMakeWorkflowClient : IAsyncDisposable
{
    private readonly Stream transport;
    private readonly JsonRpc rpc;
    private readonly Process? ownedHost;
    private readonly string? ownedSocketDirectory;
    private readonly IRemoteSshConnection? sshConnection;
    private bool disposed;

    private CMakeWorkflowClient(Stream stream, JsonRpc connection, IClientRpc target, HostIdentity identity,
        Process? ownedHost, string? ownedSocketDirectory, IRemoteSshConnection? sshConnection = null)
    {
        transport = stream;
        rpc = connection;
        Proxy = target;
        Host = identity;
        this.ownedHost = ownedHost;
        this.ownedSocketDirectory = ownedSocketDirectory;
        this.sshConnection = sshConnection;
        Workflows = new(this);
        Executions = new(this);
        Operations = new(this);
        Profiles = new(this);
        Benchmarks = new(this);
        Recipes = new(this);
        Targets = new(this);
        Campaigns = new(this);
        History = new(this);
        Toolchains = new(this);
        Provisioning = new(this);
    }

    public HostIdentity Host { get; }
    public WorkflowService Workflows { get; }
    public ExecutionService Executions { get; }
    public OperationService Operations { get; }
    public ProfileService Profiles { get; }
    public BenchmarkService Benchmarks { get; }
    public RecipeService Recipes { get; }
    public TargetService Targets { get; }
    public CampaignService Campaigns { get; }
    public HistoryService History { get; }
    public ToolchainService Toolchains { get; }
    public HostProvisioningService Provisioning { get; }

    public static async Task<CMakeWorkflowClient> ConnectAsync(ClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string workspace = Path.GetFullPath(options.Workspace);
        Stream stream = await ConnectTransportAsync(options.Endpoint, ct);
        try
        {
            return await InitializeAsync(stream, workspace, null, null, ct);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts a private Host. Disposing the client stops only this owned Host, including its active work.</summary>
    public static async Task<CMakeWorkflowClient> LaunchAsync(LaunchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string workspace = Path.GetFullPath(options.Workspace);
        string? dir = OperatingSystem.IsWindows()
            ? null
            : Path.Combine(Path.GetTempPath(), "cmw-sdk-" + Guid.NewGuid().ToString("N"));
        if (dir is not null)
        {
            _ = Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        string endpoint = options.Endpoint ?? (OperatingSystem.IsWindows()
            ? @"\\.\pipe\cmakeworkflow-" + Guid.NewGuid().ToString("N")
            : Path.Combine(dir!, "host.sock"));
        ProcessStartInfo start = new(options.Executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--endpoint");
        start.ArgumentList.Add(endpoint);
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new IOException($"Host exited with code {process.ExitCode} before accepting connections.");
                }

                try
                {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
                    using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
#pragma warning restore CA2000

                    connect.CancelAfter(TimeSpan.FromMilliseconds(150));
                    Stream stream = await ConnectTransportAsync(endpoint, connect.Token);
                    try
                    {
                        return await InitializeAsync(stream, workspace, process, dir, timeout.Token);
                    }
                    catch
                    {
                        await stream.DisposeAsync();
                        throw;
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or TimeoutException
                                              or OperationCanceledException)
                {
                    if (timeout.IsCancellationRequested)
                    {
                        throw;
                    }

                    await Task.Delay(50, timeout.Token);
                }
            }
        }
        catch
        {
#pragma warning disable CA1508 // Process.Start may throw before assignment; retain the exceptional-path cleanup guard.
            if (process is not null)
#pragma warning restore CA1508
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }

            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            throw;
        }
    }

    // StreamJsonRpc 2.26 annotates the formatter constructor because its default
    // settings may use reflection. This instance is configured immediately with
    // the generated ClientJson resolver. Keep this suppression local and exercise
    // it in a published NativeAOT consumer before shipping changes to wire DTOs.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "Formatter uses ClientJson source-generated metadata; verified by the AOT consumer smoke test.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification =
            "Formatter uses ClientJson source-generated metadata; verified by the AOT consumer smoke test.")]
    private static async Task<CMakeWorkflowClient> InitializeAsync(Stream stream, string workspace,
        Process? process, string? directory, CancellationToken ct, Stream? output = null,
        IRemoteSshConnection? ssh = null)
    {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        SystemTextJsonFormatter formatter = new()
        {
            JsonSerializerOptions = new(ClientJson.Default.Options) { TypeInfoResolver = ClientJson.Default }
        };
#pragma warning restore CA2000


#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        ApplicationRpc rpc = new(new HeaderDelimitedMessageHandler(output ?? stream, stream, formatter));
#pragma warning restore CA2000

        // Diagnostic opt-in for NativeAOT RPC smoke. A lost $/cancelRequest is
        // otherwise invisible when StreamJsonRpc dispatches it asynchronously.
        if (Environment.GetEnvironmentVariable("CMAKEWORKFLOW_RPC_TRACE") == "1")
        {
            rpc.TraceSource.Switch.Level = SourceLevels.All;
            _ = rpc.TraceSource.Listeners.Add(new TextWriterTraceListener(Console.Error));
        }

        Task<HostIdentity>? initialization = null;
        try
        {
            IClientRpc proxy = rpc.Attach<IClientRpc>();
            rpc.StartListening();
            initialization = proxy.InitializeAsync(new(1, workspace), ct);
            // RPC cancellation is cooperative: a silent peer may never acknowledge
            // it. SSH connection establishment also needs a local deadline.
            HostIdentity info = ssh is null ? await initialization : await initialization.WaitAsync(ct);
            return info.ProtocolVersion != 1
                ? throw new InvalidDataException("Unsupported Host protocol version.")
                : new(stream, rpc, proxy, info, process, directory, ssh);
        }
        catch
        {
            // Terminate/reap SSH before disposing handlers with pending pipe I/O.
            try
            {
                if (ssh is not null)
                {
                    await ssh.DisposeAsync();
                }
            }
            finally
            {
                rpc.Dispose();
                if (ssh is not null && initialization is not null)
                {
                    // Dispose completes pending RPC invocations; observe the failed
                    // handshake even when process teardown itself reported an error.
                    try { _ = await initialization; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
            }
            throw;
        }
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
            {
                throw new ArgumentException("Invalid named pipe endpoint.", nameof(endpoint));
            }

            NamedPipeClientStream pipe = new(".", endpoint[@"\\.\pipe\".Length..], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(ct);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        if (!Path.IsPathFullyQualified(endpoint) || Encoding.UTF8.GetByteCount(endpoint) > 100)
        {
            throw new ArgumentException("Unix socket endpoint must be absolute and at most 100 UTF-8 bytes.",
                nameof(endpoint));
        }


#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
#pragma warning restore CA2000

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal IClientRpc Proxy
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return field;
        }
    }

    internal Task<JsonElement> InvokePlanAsync(PlanRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return rpc.InvokeWithCancellationAsync<JsonElement>("workflow/plan", new object?[] { request }, ct);
    }

    internal void RequireCapability(string capability)
    {
        if (!Host.Capabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw new NotSupportedException($"Host does not advertise {capability}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // Closing a remote connection owns only SSH, not persistent Host work.
        try
        {
            if (sshConnection is not null)
            {
                await sshConnection.DisposeAsync();
            }
        }
        finally
        {
            rpc.Dispose();
            await transport.DisposeAsync();
        }
        if (ownedHost is not null)
        {
            try
            {
                if (!ownedHost.HasExited)
                {
                    ownedHost.Kill(entireProcessTree: true);
                    await ownedHost.WaitForExitAsync();
                }
            }
            finally
            {
                ownedHost.Dispose();
            }
        }

        if (ownedSocketDirectory is not null && Directory.Exists(ownedSocketDirectory))
        {
            Directory.Delete(ownedSocketDirectory, recursive: true);
        }
    }
}
