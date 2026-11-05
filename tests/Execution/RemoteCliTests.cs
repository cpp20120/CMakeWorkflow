using static CMakeWorkflow.IntegrationTests.TestHarness;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests;
#pragma warning restore IDE0130

// CLI input must fail before starting SSH. The existing Host SSH fixtures
// exercise the actual RPC execution, durable lookup and artifact transfer.
internal static class RemoteCliTests
{
    internal static void Run()
    {
        string help = Command(Cli, ["remote", "help"]).Output;
        Check(help.Contains("remote COMMAND", StringComparison.Ordinal) && help.Contains("--request-id", StringComparison.Ordinal) &&
              help.Contains("--artifact-id", StringComparison.Ordinal), "Remote CLI help is missing the durable contract.");

        string[] connection = [ "--destination", "unreachable.example", "--endpoint", "/tmp/host.sock",
            "--workspace", "/remote/workspace" ];
        Reject(["remote", "nope"], "Unknown remote command");
        // Connection-wide options are validated before transport-specific ones.
        Reject(["remote", "status", "--operation", "abc"], "--endpoint");
        Reject(["remote", "status", "--endpoint", "/tmp/host.sock", "--operation", "abc"], "--workspace");
        Reject(["remote", "status", "--endpoint", "/tmp/host.sock", "--workspace", "/remote/workspace",
            "--operation", "abc"], "--destination");
        Reject(["remote", "status", .. connection], "--operation");
        Reject(["remote", "status", .. connection, "--operation", "id", "--operation", "again"], "Duplicate option");
        Reject(["remote", "status", .. connection, "--operation", "id", "--jobs", "2"], "invalid for remote status");
        Reject(["remote", "start", ..connection, "--request-id", "stable", "--manifest", "/remote/workflow.json",
            "--manifest-sha256", "BAD", "--results-dir", "/remote/results"], "--manifest-sha256");
        Reject(["remote", "events", .. connection, "--operation", "id", "--limit", "501"], "--limit");
        Reject(["remote", "events", .. connection, "--operation", "id", "--after", "-1"], "--after");
        Reject(["remote", "cancel", .. connection, "--operation", "id"], "--revision");
        Reject(["remote", "cancel", .. connection, "--operation", "id", "--revision", "-1"], "--revision");
        Reject(["remote", "download", .. connection, "--operation", "id", "--artifact-id", "stdout:effect"],
            "--output");
        Reject(["remote", "find", .. connection, "--request-id", "id", "--connect-timeout-seconds", "0"],
            "--connect-timeout-seconds");
        string[] direct = [ "--transport", "sshnet", "--host", "server.invalid", "--user", "builder",
            "--key-file", "/missing/key", "--host-key-sha256", "SHA256:INVALID",
            "--endpoint", "/tmp/host.sock", "--workspace", "/remote/project" ];
        Reject(["remote", "status", .. direct, "--operation", "id", "--destination", "alias"],
            "OpenSSH-only");
        Reject(["remote", "status", .. connection, "--operation", "id", "--port", "22"],
            "SSH.NET options require");
        Reject(["remote", "status", .. direct, "--operation", "id", "--port", "0"], "--port");
        Reject(["remote", "status", .. direct, "--operation", "id", "--transport", "openssh"],
            "Duplicate option");
        Reject(["remote", "status", .. direct, "--operation", "id", "--connect-timeout-seconds", "0"],
            "--connect-timeout-seconds");
        Pass("remote CLI validation rejects malformed admission, cursor, cancellation and downloads before SSH");
    }

    private static void Reject(string[] args, string expected)
    {
        CommandResult result = Command(Cli, args, expected: 1);
        Check(result.Output.Contains(expected, StringComparison.Ordinal),
            $"Remote CLI did not reject with '{expected}': {result.Output}");
    }
}
