using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Client.Workflows;
using Renci.SshNet;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Host.Tests.Transport;

/// <summary>
/// Opt-in tests against an actual Linux sshd. These intentionally never run in
/// the default CI matrix, which has no trusted remote host or SSH credentials.
/// Each test uses the production SDK, a second independently authenticated SSH
/// connection for observation, and the installed remote CMakeWorkflow binary.
/// </summary>
internal static class SshNetLiveLifecycleTests
{
    internal static async Task RunAsync(string? scenario)
    {
        if (scenario is not (null or "handshake" or "workflow" or "persistent"))
        {
            throw new ArgumentException("Expected --sshnet-live-lifecycle-tests [handshake|workflow|persistent].");
        }

        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new ArgumentException("Missing SSH.NET live test setting: " + name);

        SshNetLaunchOptions options = new(
            Required("CMWF_SSHNET_HOST"), Required("CMWF_SSHNET_USER"), Required("CMWF_SSHNET_KEY"),
            Required("CMWF_SSHNET_PIN"), Required("CMWF_SSHNET_WORKSPACE"))
        {
            RemoteExecutable = Environment.GetEnvironmentVariable("CMWF_SSHNET_EXE") ?? "cmakeworkflow",
            Port = int.Parse(Environment.GetEnvironmentVariable("CMWF_SSHNET_PORT") ?? "22", System.Globalization.CultureInfo.CurrentCulture),
            ConnectionTimeout = TimeSpan.FromSeconds(12)
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        // The probe is independent from the channel controlled by LaunchSshNetAsync.
        // It also verifies the *same* pinned host identity, instead of bypassing SSH security.
        using Probe probe = new(options);
        _ = probe.Run("test -d " + Quote(options.Workspace) + " && test -w " + Quote(options.Workspace));

        if (scenario is null or "handshake")
        {
            await HandshakeAsync(probe, options, timeout.Token);
        }

        if (scenario is null or "workflow")
        {
            await WorkflowAsync(probe, options, timeout.Token);
        }

        if (scenario == "persistent")
        {
            await PersistentAsync(probe, options, timeout.Token);
        }

        Console.WriteLine("PASS SSH.NET live lifecycle: " + (scenario ?? "handshake, workflow"));
    }

    private static async Task PersistentAsync(Probe probe, SshNetLaunchOptions options, CancellationToken ct)
    {
        string endpoint = Environment.GetEnvironmentVariable("CMWF_SSHNET_ENDPOINT")
                          ?? throw new ArgumentException("persistent scenario requires CMWF_SSHNET_ENDPOINT.");
        SshNetHostOptions persistent = new(options.Host, options.Username, options.PrivateKeyPath,
            options.HostKeySha256, endpoint, options.Workspace)
        {
            Port = options.Port,
            RemoteExecutable = options.RemoteExecutable,
            ConnectionTimeout = options.ConnectionTimeout
        };
        int pid;
        await using (CMakeWorkflowClient first = await CMakeWorkflowClient.ConnectSshNetAsync(persistent, ct))
        {
            pid = first.Host.ProcessId;
            Check(pid > 0 && probe.Alive(pid), "Existing persistent Host process not alive.");
            Check((await first.Toolchains.DiscoverAsync(ct)).SchemaVersion == 1,
                "Persistent Host lost typed RPC compatibility.");
        }
        Check(probe.Alive(pid), "Closing an SSH.NET bridge terminated its persistent Host.");
        await using (CMakeWorkflowClient reconnected = await CMakeWorkflowClient.ConnectSshNetAsync(persistent, ct))
        {
            Check(reconnected.Host.ProcessId == pid,
                "Reconnecting to the persistent Host activated a different process.");
            Check((await reconnected.Toolchains.DiscoverAsync(ct)).SchemaVersion == 1,
                "Persistent Host failed after SSH.NET reconnect.");
        }
        Check(probe.Alive(pid), "The second SSH.NET disconnect terminated the persistent Host.");
        Console.WriteLine("PASS SSH.NET persistent Host survives two SSH exec channels and reconnect");
    }

    private static async Task HandshakeAsync(Probe probe, SshNetLaunchOptions options, CancellationToken ct)
    {
        int pid;
        await using (CMakeWorkflowClient client = await CMakeWorkflowClient.LaunchSshNetAsync(options, ct))
        {
            pid = client.Host.ProcessId;
            Check(pid > 0 && client.Host.WorkspaceDirectory == options.Workspace,
                "SSH.NET private Host handshake returned an unexpected process/workspace.");
            Check(MemoryExtensions.Contains(client.Host.Capabilities, "workflow/start"), "Private Host lacks execution capability.");
            Check((await client.Toolchains.DiscoverAsync(ct)).SchemaVersion == 1,
                "Live SSH.NET RPC corrupts a typed response.");
            Check(probe.Alive(pid), "Private Host process was not alive during the RPC session.");
        }
        await probe.WaitForExitAsync(pid, ct);
        Console.WriteLine("PASS SSH.NET private Host: real SSH handshake, read RPC, EOF and remote process exit");

        // Mutate a *valid* SHA-256 pin, so validation succeeds but the SSH handshake
        // must fail before the remote executable is ever launched.
        string rawPin = options.HostKeySha256.StartsWith("SHA256:", StringComparison.Ordinal)
            ? options.HostKeySha256[7..] : options.HostKeySha256;
        byte[] bytes = Convert.FromBase64String(rawPin + "=");
        bytes[0] ^= 0x80;
        string wrongPin = "SHA256:" + Convert.ToBase64String(bytes).TrimEnd('=');
        await ExpectConnectionRefusalAsync(options with { HostKeySha256 = wrongPin }, ct);
        Console.WriteLine("PASS SSH.NET rejects a cryptographically valid but incorrect host-key pin");

        // A fresh private key is not in the remote authorized_keys. No password,
        // ssh-agent, or fallback authentication may turn this into success.
        string wrongKey = Path.Combine(Path.GetTempPath(), "cmwf-ssh-auth-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            using RSA rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(wrongKey, rsa.ExportRSAPrivateKeyPem(), ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(wrongKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await ExpectConnectionRefusalAsync(options with { PrivateKeyPath = wrongKey }, ct);
        }
        finally
        {
            if (File.Exists(wrongKey))
            {
                File.Delete(wrongKey);
            }
        }
        Console.WriteLine("PASS SSH.NET rejects an unauthorized private key");

        // Failure in exec/startup must have a bounded outcome, not hang forever
        // waiting for JSON-RPC initialization from a process that never started.
        SshNetLaunchOptions missing = options with
        {
            RemoteExecutable = "/cmwf-f3-missing-" + Guid.NewGuid().ToString("N"),
            ConnectionTimeout = TimeSpan.FromSeconds(3)
        };
        bool refused = false;
        try
        {
            await using CMakeWorkflowClient client = await CMakeWorkflowClient.LaunchSshNetAsync(missing, ct);
        }
        catch (Exception error) when (error is IOException or TimeoutException or StreamJsonRpc.ConnectionLostException)
        {
            refused = true;
        }
        Check(refused, "A missing remote executable was accepted or failed without a transport/startup diagnostic.");
        Console.WriteLine("PASS SSH.NET missing remote executable fails within the connection deadline");
    }

    private static async Task WorkflowAsync(Probe probe, SshNetLaunchOptions options, CancellationToken ct)
    {
        // Use only a random child of the operator-provided writable workspace.
        // Never mutate the project or results of an existing workflow.
        string suffix = Guid.NewGuid().ToString("N")[..16];
        string root = options.Workspace.TrimEnd('/') + "/f3-sshnet-" + suffix;
        string source = root + "/source";
        string build = root + "/build";
        string marker = source + "/effects.txt";
        string release = source + "/release";
        string manifestPath = root + "/workflow.json";
        int pid = 0;
        bool started = false;
        _ = probe.Run("mkdir -m 700 -- " + Quote(root) + " && mkdir -- " + Quote(source) + " " + Quote(build));
        try
        {
            probe.UploadText(source + "/CMakeLists.txt", """
                                                         cmake_minimum_required(VERSION 3.26)
                                                         project(cmwf_f3 NONE)
                                                         add_custom_target(effect COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake")
                                                         """);
            probe.UploadText(source + "/effect.cmake", """
                                                       file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "started\n")
                                                       set(attempt 0)
                                                       while(NOT EXISTS "${CMAKE_CURRENT_LIST_DIR}/release")
                                                         if(attempt GREATER_EQUAL 1200)
                                                           message(FATAL_ERROR "F3 release gate timed out")
                                                         endif()
                                                         math(EXPR attempt "${attempt} + 1")
                                                         execute_process(COMMAND "${CMAKE_COMMAND}" -E sleep 0.1)
                                                       endwhile()
                                                       file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "finished\n")
                                                       """);
            _ = probe.Run("mkdir -p -- " + Quote(build + "/.cmake/api/v1/query") +
                          " && : > " + Quote(build + "/.cmake/api/v1/query/codemodel-v2"));
            _ = probe.Run("cmake -S " + Quote(source) + " -B " + Quote(build) + " -G Ninja");

            var document = new
            {
                schemaVersion = 1,
                contexts = new Dictionary<string, object> { ["w"] = new { buildDirectory = build, mode = "adopt" } },
                nodes = new[] { new { id = "effect", kind = "invoke", context = "w", target = "effect" } }
            };
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(document);
            probe.Upload(manifestPath, manifest);
            StartWorkflowOptions request = new("f3-sshnet-" + suffix, manifestPath,
                Convert.ToHexString(SHA256.HashData(manifest)), 1, root + "/results");

            await using (CMakeWorkflowClient client = await CMakeWorkflowClient.LaunchSshNetAsync(options, ct))
            {
                pid = client.Host.ProcessId;
                WorkflowOperation operation = await client.Workflows.StartAsync(request, ct);
                Check(!string.IsNullOrEmpty(operation.OperationId), "Private Host did not accept the workflow.");
                await probe.WaitForMarkerAsync(marker, ct);
                started = true;
                Check(probe.Alive(pid), "Private Host exited before intentional transport teardown.");
                // Do NOT issue operations/cancel. Closing the SSH exec channel
                // must revoke this private coordinator's outstanding work.
            }
            await probe.WaitForExitAsync(pid, ct);

            // Release any accidentally surviving child. A finished marker now
            // indicates that the process escaped the ephemeral Host teardown.
            _ = probe.Run(": > " + Quote(release));
            await Task.Delay(700, ct);
            string effects = probe.Read(marker);
            Check(effects.Split('\n', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(["started"]),
                "Private Host left a running child, duplicated an effect, or dispatched after disconnect: " + effects);
            Console.WriteLine("PASS SSH.NET private workflow: accepted once, disconnected mid-execution, host/child stopped");
        }
        finally
        {
            // Release the gated command even if the assertion failed, then clean
            // only the test-created directory. Never delete the base workspace.
            try
            {
                if (started)
                {
                    _ = probe.Run(": > " + Quote(release));
                }

                if (pid > 0)
                {
                    await probe.WaitForExitAsync(pid, ct);
                }

                _ = probe.Run("rm -rf -- " + Quote(root));
            }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync("F3 cleanup incomplete for " + root + ": " + error.Message);
            }
#pragma warning restore CA1031

        }
    }

    private static async Task ExpectConnectionRefusalAsync(SshNetLaunchOptions options, CancellationToken ct)
    {
        try
        {
            await using CMakeWorkflowClient ignored = await CMakeWorkflowClient.LaunchSshNetAsync(options, ct);
        }
        catch (SshConnectionException)
        {
            return;
        }
        throw new System.Diagnostics.UnreachableException("SSH.NET accepted incorrect authentication material.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }

    private static string Quote(string input) => "'" + input.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed class Probe : IDisposable
    {
        private readonly PrivateKeyFile key;
        private readonly SshClient client;

        internal Probe(SshNetLaunchOptions options)
        {
            key = new PrivateKeyFile(options.PrivateKeyPath);

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
            PrivateKeyConnectionInfo connection = new(options.Host, options.Port, options.Username, key)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
#pragma warning restore CA2000

            client = new SshClient(connection);
            string pin = options.HostKeySha256.StartsWith("SHA256:", StringComparison.Ordinal)
                ? options.HostKeySha256[7..] : options.HostKeySha256;
            byte[] expected = Convert.FromBase64String(pin + "=");
            client.HostKeyReceived += (_, args) =>
                args.CanTrust = CryptographicOperations.FixedTimeEquals(SHA256.HashData(args.HostKey), expected);
            try { client.Connect(); }
            catch { client.Dispose(); key.Dispose(); throw; }
        }

        internal string Run(string shell)
        {
            using SshCommand command = client.CreateCommand(shell);
            command.CommandTimeout = TimeSpan.FromSeconds(15);
            string output = command.Execute();
            return command.ExitStatus != 0
                ? throw new IOException("Remote test command failed (exit " + command.ExitStatus + "): " +
                                        shell + "\n" + command.Error)
                : output;
        }

        internal string Read(string path) => Run("cat -- " + Quote(path));

        internal void UploadText(string path, string text) => Upload(path, Encoding.UTF8.GetBytes(text));

        internal void Upload(string path, byte[] bytes)
        {
            // No SFTP service or local OpenSSH dependency. Base64 is a literal
            // single argument, decoded on the authenticated remote POSIX shell.
            string encoded = Convert.ToBase64String(bytes);
            _ = Run("printf '%s' " + Quote(encoded) + " | base64 -d > " + Quote(path));
        }

        internal bool Alive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }

            using SshCommand command = client.CreateCommand("ps -o stat= -p " + pid);
            command.CommandTimeout = TimeSpan.FromSeconds(5);
            string state = command.Execute().Trim();
            return command.ExitStatus == 0 && state.Length != 0 && state[0] != 'Z';
        }

        internal async Task WaitForExitAsync(int pid, CancellationToken ct)
        {
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(12));
            while (Alive(pid))
            {
                await Task.Delay(150, wait.Token);
            }
        }

        internal async Task WaitForMarkerAsync(string marker, CancellationToken ct)
        {
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                using SshCommand command = client.CreateCommand("test -f " + Quote(marker) +
                                                                " && grep -qx 'started' " + Quote(marker));
                command.CommandTimeout = TimeSpan.FromSeconds(5);
                using CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
                commandTimeout.CancelAfter(command.CommandTimeout);
                await command.ExecuteAsync(commandTimeout.Token);
                if (command.ExitStatus == 0)
                {
                    return;
                }

                await Task.Delay(100, wait.Token);
            }
        }

        public void Dispose()
        {
            client.Dispose();
            key.Dispose();
        }
    }
}