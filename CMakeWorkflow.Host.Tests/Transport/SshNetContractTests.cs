using CMakeWorkflow.Client;
using CMakeWorkflow.Host;
using Nerdbank.Streams;
using StreamJsonRpc;

internal static class SshNetContractTests
{
    private static readonly string ValidPin = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=');

    // Invalid arguments must be rejected before reading private keys or opening sockets.
    internal static async Task RunAsync()
    {
        string missingKey = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "cmwf-sshnet-test-" + Guid.NewGuid().ToString("N"), "id_ed25519"));
        SshNetHostOptions options = new("ssh.invalid", "tester", missingKey,
            ValidPin, "/tmp/cmw.sock", "/srv/project");
        await RejectAsync<ArgumentException>(options with { HostKeySha256 = "SHA256:BAD" });
        await RejectAsync<ArgumentException>(options with { HostKeySha256 = "" });
        await RejectAsync<ArgumentException>(options with { Endpoint = "/tmp/../escape" });
        await RejectAsync<ArgumentException>(options with { Workspace = @"C:\project" });
        await RejectAsync<ArgumentException>(options with { Username = "user name" });
        await RejectAsync<ArgumentOutOfRangeException>(options with { Port = 0 });
        await RejectAsync<ArgumentOutOfRangeException>(options with { ConnectionTimeout = TimeSpan.Zero });
        SshNetLaunchOptions ephemeral = new(options.Host, options.Username, missingKey,
            ValidPin, options.Workspace);
        await RejectEphemeralAsync<ArgumentException>(ephemeral with { HostKeySha256 = "SHA256:BAD" });
        await RejectEphemeralAsync<ArgumentException>(ephemeral with { Workspace = "relative" });
        await RejectEphemeralAsync<ArgumentException>(ephemeral with { PrivateKeyPath = "id_ed25519" });
        await RejectEphemeralAsync<ArgumentOutOfRangeException>(ephemeral with { Port = 0 });
        await RejectEphemeralAsync<ArgumentOutOfRangeException>(ephemeral with { ConnectionTimeout = TimeSpan.Zero });
        await VerifyPrivateHostAsync();
        Console.WriteLine("PASS SSH.NET options and private stdio Host: pin, paths, handshake, EOF shutdown");
    }

    private static async Task RejectAsync<T>(SshNetHostOptions options) where T : Exception
    {
        try { _ = await CMakeWorkflowClient.ConnectSshNetAsync(options); }
        catch (T) { return; }
        throw new System.Diagnostics.UnreachableException("SSH.NET did not reject " + typeof(T).Name + " before connecting.");
    }

    private static async Task RejectEphemeralAsync<T>(SshNetLaunchOptions options) where T : Exception
    {
        try { _ = await CMakeWorkflowClient.LaunchSshNetAsync(options); }
        catch (T) { return; }
        throw new System.Diagnostics.UnreachableException("Private SSH.NET Host did not reject " + typeof(T).Name + " before connecting.");
    }

    private static async Task VerifyPrivateHostAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        string root = Path.Combine(Path.GetTempPath(), "cmw-ephemeral-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        (Stream? clientStream, Stream? hostStream) = FullDuplexStream.CreatePair();
        using (clientStream)
        using (hostStream)
        {

#pragma warning disable CA2025 // Disposing the transport intentionally signals EOF; the forwarding/server task is awaited before leaving the ownership scope.
            Task server = HostServer.RunStdioAsync(hostStream, hostStream, timeout.Token);
#pragma warning restore CA2025

            try
            {
                using (JsonRpc rpc = HostConnection.Create(clientStream))
                {
                    IHostRpc proxy = rpc.Attach<IHostRpc>();
                    rpc.StartListening();
                    HostInfo identity = await proxy.InitializeAsync(new(1, root), timeout.Token);
                    if (identity.WorkspaceDirectory != root || !identity.Capabilities.Contains("workflow/start"))
                    {
                        throw new System.Diagnostics.UnreachableException("Private Host handshake returned invalid identity/capabilities.");
                    }
                }
                await clientStream.DisposeAsync();
                await server.WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
            }
            finally
            {
                await clientStream.DisposeAsync();
                if (!server.IsCompleted)
                {
                    await timeout.CancelAsync();
                }

                await server.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        try
        {
            _ = await SshTransportTests.RejectAsync<ArgumentException>(() =>
                HostCommand.ExecuteAsync(["serve", "--stdio", "--endpoint", "/tmp/unused"], default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Opt-in integration against a real sshd and a persistent remote Host.
    // This is intentionally not a mock OpenSSH wrapper; it exercises the SSH.NET
    // key exchange, pinned server identity, exec stdin/stdout and JSON-RPC.
    internal static async Task RunLiveAsync()
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new ArgumentException("Missing live SSH.NET test setting: " + name);
        SshNetHostOptions options = new(
            Env("CMWF_SSHNET_HOST"), Env("CMWF_SSHNET_USER"), Env("CMWF_SSHNET_KEY"),
            Env("CMWF_SSHNET_PIN"), Env("CMWF_SSHNET_ENDPOINT"), Env("CMWF_SSHNET_WORKSPACE"))
        {
            Port = int.Parse(Environment.GetEnvironmentVariable("CMWF_SSHNET_PORT") ?? "22", System.Globalization.CultureInfo.CurrentCulture),
            RemoteExecutable = Environment.GetEnvironmentVariable("CMWF_SSHNET_EXE") ?? "cmakeworkflow"
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using (CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectSshNetAsync(options, timeout.Token))
        {
            if (!client.Host.Capabilities.Contains("operations/get"))
            {
                throw new System.Diagnostics.UnreachableException("SSH.NET bridge did not negotiate Host capabilities.");
            }

            if (!client.Host.WorkspaceDirectory.Equals(options.Workspace, StringComparison.Ordinal))
            {
                throw new System.Diagnostics.UnreachableException("SSH.NET bridge initialized the wrong remote workspace.");
            }

            ToolchainInventory tools = await client.Toolchains.DiscoverAsync(timeout.Token);
            if (tools.SchemaVersion != 1)
            {
                throw new System.Diagnostics.UnreachableException("SSH.NET typed RPC payload corrupted.");
            }
        }
        Console.WriteLine("PASS SSH.NET live: pinned key, exec binary RPC, read and teardown");
    }
}
