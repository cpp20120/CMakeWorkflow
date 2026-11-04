using CMakeWorkflow.Client;

internal static class SshNetContractTests
{
    private static readonly string ValidPin = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=');

    // Invalid arguments must be rejected before reading private keys or opening sockets.
    internal static async Task RunAsync()
    {
        var missingKey = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "cmwf-sshnet-test-" + Guid.NewGuid().ToString("N"), "id_ed25519"));
        var options = new SshNetHostOptions("ssh.invalid", "tester", missingKey,
            ValidPin, "/tmp/cmw.sock", "/srv/project");
        await RejectAsync<ArgumentException>(options with { HostKeySha256 = "SHA256:BAD" });
        await RejectAsync<ArgumentException>(options with { HostKeySha256 = "" });
        await RejectAsync<ArgumentException>(options with { Endpoint = "/tmp/../escape" });
        await RejectAsync<ArgumentException>(options with { Workspace = @"C:\project" });
        await RejectAsync<ArgumentException>(options with { Username = "user name" });
        await RejectAsync<ArgumentOutOfRangeException>(options with { Port = 0 });
        await RejectAsync<ArgumentOutOfRangeException>(options with { ConnectionTimeout = TimeSpan.Zero });
        Console.WriteLine("PASS SSH.NET options: host-key pin, remote path, port, user and timeout");
    }

    private static async Task RejectAsync<T>(SshNetHostOptions options) where T : Exception
    {
        try { await CMakeWorkflowClient.ConnectSshNetAsync(options); }
        catch (T) { return; }
        throw new Exception("SSH.NET did not reject " + typeof(T).Name + " before connecting.");
    }

    // Opt-in integration against a real sshd and a persistent remote Host.
    // This is intentionally not a mock OpenSSH wrapper; it exercises the SSH.NET
    // key exchange, pinned server identity, exec stdin/stdout and JSON-RPC.
    internal static async Task RunLiveAsync()
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new ArgumentException("Missing live SSH.NET test setting: " + name);
        var options = new SshNetHostOptions(
            Env("CMWF_SSHNET_HOST"), Env("CMWF_SSHNET_USER"), Env("CMWF_SSHNET_KEY"),
            Env("CMWF_SSHNET_PIN"), Env("CMWF_SSHNET_ENDPOINT"), Env("CMWF_SSHNET_WORKSPACE"))
        {
            Port = int.Parse(Environment.GetEnvironmentVariable("CMWF_SSHNET_PORT") ?? "22"),
            RemoteExecutable = Environment.GetEnvironmentVariable("CMWF_SSHNET_EXE") ?? "cmakeworkflow"
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var client = await CMakeWorkflowClient.ConnectSshNetAsync(options, timeout.Token))
        {
            if (!client.Host.Capabilities.Contains("operations/get"))
                throw new Exception("SSH.NET bridge did not negotiate Host capabilities.");
            if (!client.Host.WorkspaceDirectory.Equals(options.Workspace, StringComparison.Ordinal))
                throw new Exception("SSH.NET bridge initialized the wrong remote workspace.");
            var tools = await client.Toolchains.DiscoverAsync(timeout.Token);
            if (tools.SchemaVersion != 1)
                throw new Exception("SSH.NET typed RPC payload corrupted.");
        }
        Console.WriteLine("PASS SSH.NET live: pinned key, exec binary RPC, read and teardown");
    }
}
