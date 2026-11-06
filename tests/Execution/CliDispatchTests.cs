using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Execution;

internal static class CliDispatchTests
{
    internal static void Run()
    {
        string[][] invalidCommands =
        [
            [], ["unknown"], ["workflow"], ["workflow", "report"],
            ["target", "unknown"], ["context", "inspect", ".", "--invalid"]
        ];
        foreach (string[] args in invalidCommands)
        {
            Check(Command(Cli, args, 2).Output.Contains("Usage: CMakeWorkflow", StringComparison.Ordinal),
                "Invalid command routing lost the top-level usage/exit contract");
        }

        Check(RunCli(1, "workflow", "resume").Contains("Usage: workflow resume", StringComparison.Ordinal),
            "Resume argument validation changed");
        Check(RunCli(1, "target", "run").Contains("--build-dir and --target are required", StringComparison.Ordinal),
            "Target argument validation changed");
        Check(RunCli(1, "profile", "analyze") == RunCli(1, "dagflow", "profile", "analyze"),
            "Profile aliases no longer dispatch to the same handler");
        Check(RunCli(1, "setup", "--invalid") == RunCli(1, "tooling", "setup", "--invalid"),
            "Setup alias no longer dispatches to tooling setup");
        string[][] historyCommands = [["workflow", "run", "absent.json"], ["profile", "run"], ["profile", "replay"]];
        foreach (string[] prefix in historyCommands)
        {
            string output = Command(Cli, [.. prefix, "--no-history", "--history-db", "unused.sqlite"], 1).Output;
            Check(output.Contains("--no-history and --history-db cannot be combined", StringComparison.Ordinal),
                "History options were not validated before command effects");
        }

        using TempTree temp = new();
        string manifest = Write(Path.Combine(temp.Path, "invalid.json"), "{invalid");
        Check(RunCli(1, "workflow", "plan", manifest).Contains("[CMakeWorkflow]", StringComparison.Ordinal),
            "Malformed JSON no longer has a CLI diagnostic");
        _ = Write(Path.Combine(temp.Path, "run.json"), "null");
        Check(RunCli(1, "workflow", "resume", temp.Path).Contains("Invalid run report", StringComparison.Ordinal),
            "Invalid checkpoint no longer has a CLI diagnostic");
        string failedSsh = RunCli(1, "remote", "status", "--destination", "tester@invalid.example",
            "--endpoint", "/remote/rpc.sock", "--workspace", "/remote/workspace", "--operation", "absent",
            "--ssh-executable", Path.Combine(temp.Path, "missing-ssh"));
        Check(failedSsh.Contains("[CMakeWorkflow]", StringComparison.Ordinal) &&
              !failedSsh.Contains("Unhandled exception", StringComparison.Ordinal),
            "Local SSH startup failure escaped the CLI transport diagnostic");

        Console.WriteLine("PASS CLI routing, aliases, expected failure diagnostics and history option validation");
    }
}
