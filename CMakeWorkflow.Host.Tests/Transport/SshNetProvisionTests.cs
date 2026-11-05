using System.Security.Cryptography;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Provisioning;
using Renci.SshNet;

/// <summary>Opt-in real sshd/SFTP Host provision and idempotent activation.</summary>
internal static class SshNetProvisionTests
{
    internal static async Task RunLiveAsync()
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new ArgumentException("Missing F2 live setting: " + name);
        string workspace = Env("CMWF_SSHNET_WORKSPACE");
        string root = workspace.TrimEnd('/') + "/f2-provision-" + Guid.NewGuid().ToString("N")[..12];
        SshNetProvisionOptions options = new(Env("CMWF_SSHNET_HOST"), Env("CMWF_SSHNET_USER"),
            Env("CMWF_SSHNET_KEY"), Env("CMWF_SSHNET_PIN"), workspace,
            Env("CMWF_SSHNET_PUBLISH"), root, "linux-x64")
        {
            Port = int.Parse(Environment.GetEnvironmentVariable("CMWF_SSHNET_PORT") ?? "22", System.Globalization.CultureInfo.CurrentCulture),
            ConnectionTimeout = TimeSpan.FromSeconds(12)
        };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(110));
        CancellationToken ct = deadline.Token;
        using PrivateKeyFile key = new(options.PrivateKeyPath);

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        using SshClient ssh = new(new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, key)
        { Timeout = options.ConnectionTimeout });
#pragma warning restore CA2000

        string pin = options.HostKeySha256.StartsWith("SHA256:", StringComparison.Ordinal)
            ? options.HostKeySha256[7..] : options.HostKeySha256;
        byte[] expected = Convert.FromBase64String(pin + "=");
        ssh.HostKeyReceived += (_, args) =>
            args.CanTrust = CryptographicOperations.FixedTimeEquals(SHA256.HashData(args.HostKey), expected);
        ssh.Connect();
        try
        {
            using (SshCommand check = ssh.CreateCommand("test ! -e " + Q(root)))
            {
                await check.ExecuteAsync(ct);
                Check(check.ExitStatus == 0, "F2 test install root already exists.");
            }
            SshNetProvisionPlan plan = SshNetHostProvisioner.Plan(options, ct);
            Check(plan.Files.Length > 0 && !plan.AlreadyActive && plan.ToolProbes.Length == 4,
                "Initial read-only provision plan is invalid.");
            bool rejected = false;
            try
            {
                _ = SshNetHostProvisioner.Apply(options, new string('0', 64), ct);
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Wrong approval digest was not rejected.");
            using (SshCommand check = ssh.CreateCommand("test ! -e " + Q(root)))
            {
                await check.ExecuteAsync(ct);
                Check(check.ExitStatus == 0, "Rejected approval mutated the remote target.");
            }
            SshNetProvisionReceipt installed = SshNetHostProvisioner.Apply(options, plan.ApprovalSha256, ct);
            Check(!installed.AlreadyActive && installed.Executable == root + "/current/CMakeWorkflow",
                "First F2 application failed to activate versioned Host.");
            await using (CMakeWorkflowClient client = await CMakeWorkflowClient.LaunchSshNetAsync(
                new SshNetLaunchOptions(options.Host, options.Username, options.PrivateKeyPath,
                    options.HostKeySha256, workspace)
                {
                    RemoteExecutable = installed.Executable,
                    Port = options.Port,
                    ConnectionTimeout = options.ConnectionTimeout
                }, ct))
            {
                Check(client.Host.Capabilities.Contains("workflow/start"), "Provisioned Host has no workflow service.");
                Check((await client.Toolchains.DiscoverAsync(ct)).SchemaVersion == 1,
                    "Provisioned Host cannot perform typed RPC.");
            }
            SshNetProvisionReceipt repeated = SshNetHostProvisioner.Apply(options, plan.ApprovalSha256, ct);
            Check(repeated.AlreadyActive && repeated.ReleaseDirectory == installed.ReleaseDirectory,
                "Repeated application was not idempotent.");
            Console.WriteLine("PASS F2 provisioning: read-only plan, approval, SFTP SHA256, activation, executable Host RPC, idempotence");
        }
        finally
        {
            // The test owns exactly this random child, not its parent workspace.
            using SshCommand cleanup = ssh.CreateCommand("rm -rf -- " + Q(root));
            cleanup.CommandTimeout = TimeSpan.FromSeconds(10);
            using CancellationTokenSource cleanupTimeout = new(cleanup.CommandTimeout);
            await cleanup.ExecuteAsync(cleanupTimeout.Token);
            if (cleanup.ExitStatus != 0)
            {

#pragma warning disable CA2219 // An owned-session or test cleanup failure must surface rather than report successful cleanup.
                throw new IOException("Cannot remove isolated F2 test deployment.");
#pragma warning restore CA2219

            }
        }
    }

    private static string Q(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }
}
