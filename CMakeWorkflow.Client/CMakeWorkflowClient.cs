using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Client.Campaigns;
using CMakeWorkflow.Client.History;
using CMakeWorkflow.Client.Profiling;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Client.Provisioning;
using CMakeWorkflow.Client.Recipes;
using CMakeWorkflow.Client.Toolchains;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Client.Workflows;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

/// <summary>Owns one initialized RPC connection and its service facades.</summary>
/// <remarks>
/// Service facades and operation handles borrow this client and become unusable after disposal.
/// Request cancellation is cooperative RPC cancellation, not a durable request to stop accepted execution.
/// An interrupted mutation may already have committed; the SDK does not reconnect or retry it automatically.
/// For uncertain starts, reconnect to the same Host workspace and find the saved ClientRequestId before deciding what to do.
/// Connected persistent Hosts outlive this client. Launched private Hosts are owned by the client and stop with its lifetime.
/// </remarks>
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

    /// <summary>Identity and advertised capabilities returned by initialization.</summary>
    public HostIdentity Host { get; }
    /// <summary>Manifest planning, preflight and durable workflow admission on this connection.</summary>
    public WorkflowService Workflows { get; }
    /// <summary>Domain-qualified durable execution admission on this connection.</summary>
    public ExecutionService Executions { get; }
    /// <summary>Workspace operation lookup and handles bound to this client lifetime.</summary>
    public OperationService Operations { get; }
    /// <summary>Profile preparation, replay and analysis on the Host.</summary>
    public ProfileService Profiles { get; }
    /// <summary>Benchmark recipe preparation on the Host.</summary>
    public BenchmarkService Benchmarks { get; }
    /// <summary>Typed recipe preparation on the Host.</summary>
    public RecipeService Recipes { get; }
    /// <summary>Target resolution and preparation on the Host.</summary>
    public TargetService Targets { get; }
    /// <summary>Host campaign operations.</summary>
    public CampaignService Campaigns { get; }
    /// <summary>Host history queries and imports.</summary>
    public HistoryService History { get; }
    /// <summary>Host-local toolchain discovery and resolution.</summary>
    public ToolchainService Toolchains { get; }
    /// <summary>Host provisioning services associated with this connection.</summary>
    public HostProvisioningService Provisioning { get; }

    /// <summary>Connects once to an existing local Host and initializes its workspace.</summary>
    /// <remarks>
    /// The returned client owns the connection, not the Host. Failed initialization releases the transport.
    /// Workspace is resolved locally; endpoint validation follows the current platform transport.
    /// </remarks>
    /// <param name="options">Local endpoint and workspace to initialize.</param>
    /// <param name="ct">Cancels connection/initialization; no workflow operation is submitted.</param>
    /// <returns>An initialized client; the caller must asynchronously dispose it.</returns>
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

    /// <summary>Starts and owns a private local Host, then initializes a connection.</summary>
    /// <remarks>
    /// Startup probes retry for up to 15 seconds; these retries do not submit workflow operations.
    /// Startup failure kills the owned process tree and removes the temporary socket directory.
    /// Disposal kills this Host and its active process tree; durable records may require recovery afterward.
    /// </remarks>
    /// <param name="options">Executable, local workspace and optional endpoint.</param>
    /// <param name="ct">Cancels startup and triggers cleanup of the process started here.</param>
    /// <returns>An initialized client owning the launched Host; the caller must asynchronously dispose it.</returns>
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

    /// <summary>Closes owned transport resources and, when launched here, the private Host.</summary>
    /// <remarks>
    /// Repeated calls after disposal return immediately. Service facades and operation handles borrow this lifetime.
    /// For a connected persistent Host, closing the connection does not request durable operation cancellation.
    /// For a private local Host the process tree is killed; a private SSH Host stops when its channel closes.
    /// This method does not wait for persistent operations to finish or guarantee terminal finalization.
    /// </remarks>
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
