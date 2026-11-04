using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

// CLI input must fail before starting SSH. The existing Host SSH fixtures
// exercise the actual RPC execution, durable lookup and artifact transfer.
internal static class RemoteCliTests
{
    internal static void Run()
    {
        var help = Command(Cli, ["remote", "help"]).Output;
        Check(help.Contains("remote COMMAND") && help.Contains("--request-id") &&
              help.Contains("--artifact-id"), "Remote CLI help is missing the durable contract.");

        var connection = new[] { "--destination", "unreachable.example", "--endpoint", "/tmp/host.sock",
            "--workspace", "/remote/workspace" };
        Reject(["remote", "nope"], "Unknown remote command");
        Reject(["remote", "status", "--operation", "abc"], "--destination");
        Reject(["remote", "status", ..connection], "--operation");
        Reject(["remote", "status", ..connection, "--operation", "id", "--operation", "again"], "Duplicate option");
        Reject(["remote", "status", ..connection, "--operation", "id", "--jobs", "2"], "invalid for remote status");
        Reject(["remote", "start", ..connection, "--request-id", "stable", "--manifest", "/remote/workflow.json",
            "--manifest-sha256", "BAD", "--results-dir", "/remote/results"], "--manifest-sha256");
        Reject(["remote", "events", ..connection, "--operation", "id", "--limit", "501"], "--limit");
        Reject(["remote", "events", ..connection, "--operation", "id", "--after", "-1"], "--after");
        Reject(["remote", "cancel", ..connection, "--operation", "id"], "--revision");
        Reject(["remote", "cancel", ..connection, "--operation", "id", "--revision", "-1"], "--revision");
        Reject(["remote", "download", ..connection, "--operation", "id", "--artifact-id", "stdout:effect"],
            "--output");
        Reject(["remote", "find", ..connection, "--request-id", "id", "--connect-timeout-seconds", "0"],
            "--connect-timeout-seconds");
        Pass("remote CLI validation rejects malformed admission, cursor, cancellation and downloads before SSH");
    }

    private static void Reject(string[] args, string expected)
    {
        var result = Command(Cli, args, expected: 1);
        Check(result.Output.Contains(expected, StringComparison.Ordinal),
            $"Remote CLI did not reject with '{expected}': {result.Output}");
    }
}
