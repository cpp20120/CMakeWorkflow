using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests.Execution;
#pragma warning restore IDE0130

internal static class RecoveryTests
{
    private static bool TryReadPid(string path, out int pid)
    {
        pid = 0;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return int.TryParse(reader.ReadToEnd().Trim(), out pid) && pid > 0;
        }
        catch (IOException)
        {
            return false;
        } // Writer is still publishing the marker.
    }

    internal static void Run()
    {
        using TempTree temp = new();
        string source = Path.Combine(temp.Path, "source space");
        string build = Path.Combine(temp.Path, "build space");
        CopyFixture("workflow", source);
        Configure(source, build);
        string effects = Path.Combine(build, "effect-invocations.txt");
        int Count() => File.Exists(effects) ? File.ReadAllLines(effects).Length : 0;
        foreach ((string? point, string? decision, string? faultNode) in new[]
                 {
                     ("before-dispatch", "retry", "effect"),
                     ("checkpoint-flushed", "retry", "effect"),
                     ("checkpoint-replaced", "retry", "effect"),
                     ("after-checkpoint", "retry", "effect"),
                     ("during-process", "retry", "effect"),
                     ("after-execution", "retry", "effect"),
                     ("after-execution", "succeeded", "effect"),
                     ("during-process", "succeeded", "effect"),
                     ("after-execution", "succeeded", "build")
                 })
        {
            // Windows uses MoveFileEx WRITE_THROUGH as one operation, so there
            // is no separate post-rename/pre-directory-sync fault boundary.
            if (OperatingSystem.IsWindows() && point == "checkpoint-replaced")
            {
                continue;
            }

            string caseRoot = Path.Combine(temp.Path, point + "-" + decision + "-" + faultNode);
            _ = Directory.CreateDirectory(caseRoot);
            string gate = Path.Combine(caseRoot, "release gate");
            if (point != "during-process")
            {
                File.WriteAllText(gate, "release");
            }

            JsonObject doc = J(/*lang=json,strict*/ """
                        {"schemaVersion":1,"contexts":{"worker":{"buildDirectory":"unused","mode":"adopt"}},"nodes":[
                          {"id":"build","kind":"build","context":"worker","target":"first"},
                          {"id":"effect","kind":"run","from":"build","arguments":[]},
                          {"id":"after","kind":"invoke","context":"worker","target":"existing_action","dependsOn":["effect"]}
                        ]}
                        """);
            doc["contexts"]!["worker"]!["buildDirectory"] = build;
            doc["nodes"]![1]!["arguments"] = new JsonArray("effect", gate);
            string manifest = Write(Path.Combine(caseRoot, "workflow.json"), doc.ToJsonString());
            string results = Path.Combine(caseRoot, "results");
            string marker = Path.Combine(caseRoot, "fault.ready");
            string ready = Path.Combine(build, "ready.pid");
            File.Delete(ready);
            int before = Count();
            ProcessStartInfo info = new(Cli)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string? arg in new[] { "workflow", "run", manifest, "--results-dir", results, "--no-history" })
            {
                info.ArgumentList.Add(arg);
            }

            info.Environment["CMAKEWORKFLOW_TEST_FAULT"] = point;
            info.Environment["CMAKEWORKFLOW_TEST_NODE"] = faultNode;
            info.Environment["CMAKEWORKFLOW_TEST_MARKER"] = marker;
            using Process process = Process.Start(info) ?? throw new UnreachableException("Cannot start fault-injection runner");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Process? child = null;
            string journal;
            try
            {
                Stopwatch deadline = Stopwatch.StartNew();
                while (true)
                {
                    if (point == "during-process")
                    {
                        if (TryReadPid(ready, out int pid))
                        {
                            try
                            {
                                child = Process.GetProcessById(pid);
                                break;
                            }
                            catch (ArgumentException)
                            {
                                /* Worker exited before opening its process handle. */
                            }
                        }
                    }
                    else if (File.Exists(marker))
                    {
                        break;
                    }

                    Check(!process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30),
                        $"Fault boundary {point}/{faultNode} not reached; use a RecoveryFaultInjection=true CLI build");
                    Thread.Sleep(20);
                }

                journal = Directory.EnumerateFiles(results, "run.json", SearchOption.AllDirectories).Single();
                JsonObject admitted = J(File.ReadAllText(journal));
                if (point is "after-checkpoint" or "during-process" or "after-execution")
                {
                    Check(Status(admitted, faultNode) == "running", "Dispatch has no committed admission record");
                }

                process.Kill(); // abrupt runner death, independently of child lifetime
                Check(process.WaitForExit(10000), "Fault runner did not stop");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                Check(process.WaitForExit(10000), "Fault runner cleanup timed out");
                if (child is not null)
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }

                    Check(child.WaitForExit(5000), "Fault child cleanup timed out");
                    child.Dispose();
                }

                Check(Task.WaitAll([stdout, stderr], 5000), "Fault output did not drain");
            }

            int executed = faultNode == "effect" && point is "during-process" or "after-execution" ? 1 : 0;
            Check(Count() == before + executed, "Actual process count disagrees with fault boundary");
            string directory = Path.GetDirectoryName(journal)!;
            _ = RunCli(4, "workflow", "resume", directory);
            Check(Count() == before + executed, "Quarantine replayed an operation");
            _ = RunCli(1, "workflow", "resume", directory);
            byte[] quarantined = File.ReadAllBytes(journal);
            JsonObject checkpoint = J(System.Text.Encoding.UTF8.GetString(quarantined));
            string? node = Status(checkpoint, faultNode) == "outcome_unknown" ? faultNode : null;
            string hash = Convert.ToHexString(SHA256.HashData(quarantined));
            List<string> args =
            [
                "workflow", "reconcile", directory, "--decision", decision,
                "--reason", "Verified fixture side effects and stopped the old process", "--expected-checkpoint", hash,
                "--confirm-stopped", "--json"
            ];
            if (node is not null)
            {
                args.AddRange(["--node", node]);
            }

            string[] invalid = [.. args];
            invalid[Array.IndexOf(invalid, hash)] = new string('0', 64);
            _ = RunCli(1, invalid);
            _ = RunCli(1, [.. args.Where(a => a != "--confirm-stopped")]);
            using (FileStream lease = new(Path.Combine(directory, ".workflow-run.lock"), FileMode.Open,
                       FileAccess.ReadWrite, FileShare.None))
            {
                _ = RunCli(1, [.. args]);
            }

            Check(File.ReadAllBytes(journal).SequenceEqual(quarantined), "Rejected decision modified the checkpoint");
            JsonObject reconciled;
            if (point == "after-execution" && decision == "succeeded" && faultNode == "effect")
            {
                CrashReconcile(args, "checkpoint-flushed", caseRoot);
                Check(File.ReadAllBytes(journal).SequenceEqual(quarantined),
                    "Uncommitted audit replaced the old checkpoint");
                if (!OperatingSystem.IsWindows())
                {
                    CrashReconcile(args, "checkpoint-replaced", caseRoot);
                    reconciled = J(File.ReadAllText(journal));
                }
                else
                {
                    reconciled = J(RunCli([.. args]));
                }
            }
            else
            {
                reconciled = J(RunCli([.. args]));
            }

            Check(S(reconciled["status"]) == "needs_attention" && S(reconciled["recoveryPhase"]) == "reconciled",
                "Reconciliation did not allow explicit resume");
            JsonNode audit = reconciled["reconciliations"]!.AsArray().Single()!;
            Check(S(audit["checkpointSha256"]) == hash && S(audit["reason"]).Contains("Verified fixture", StringComparison.Ordinal)
                                                       && S(audit["actor"]).Length > 0 &&
                                                       S(audit["decision"]) == decision,
                "Decision audit is incomplete");
            if (node is not null)
            {
                Check(S(audit["previousNode"]!["status"]) == "outcome_unknown", "Prior node was not retained in audit");
            }

            Check(Count() == before + executed, "Reconcile implicitly launched work");
            byte[] committed = File.ReadAllBytes(journal);
            _ = RunCli(1, [.. args]);
            Check(File.ReadAllBytes(journal).SequenceEqual(committed), "Duplicate/stale decision was not rejected");
            File.WriteAllText(gate, "release");
            _ = RunCli("workflow", "resume", directory);
            JsonObject completed = J(File.ReadAllText(journal));
            AllSucceeded(completed);
            Check(completed["reconciliations"]!.AsArray().Count == 1, "Resume lost reconciliation audit");
            Check(Count() == before + executed + (decision == "retry" || faultNode == "build" ? 1 : 0),
                "Resume launched the wrong number of processes");
            if (decision == "succeeded")
            {
                Check(
                    string.IsNullOrEmpty(S(Item(completed, faultNode)["exitCode"])) &&
                    string.IsNullOrEmpty(S(Item(completed, faultNode)["finishedAt"])),
                    "Manual acceptance fabricated process evidence");
                if (faultNode == "build")
                {
                    Check(S(Item(completed, "build")["buildVerification"]!["verification"]) == "ManuallyAccepted",
                        "Manual build acceptance presented as observed backend success");
                }
            }

            if (node == "effect" && decision == "retry")
            {
                Check(S(Item(completed, "effect")["stdout"]).Contains("attempt-2", StringComparison.Ordinal),
                    "Retry overwrites prior attempt logs");
            }

            Pass($"recovery {point}/{faultNode} -> {decision}; launches {executed} before, {Count() - before} total");
        }

        foreach (string? failure in OperatingSystem.IsWindows()
                     ? new[] { "checkpoint-flushed" }
                     : ["checkpoint-flushed", "checkpoint-directory-sync"])
        {
            string caseRoot = Path.Combine(temp.Path, "io-" + failure);
            JsonObject doc = J(/*lang=json,strict*/ """
                        {"schemaVersion":1,"contexts":{"worker":{"buildDirectory":"unused","mode":"adopt"}},"nodes":[
                          {"id":"build","kind":"build","context":"worker","target":"first"},
                          {"id":"effect","kind":"run","from":"build","arguments":["effect"]}
                        ]}
                        """);
            doc["contexts"]!["worker"]!["buildDirectory"] = build;
            string manifest = Write(Path.Combine(caseRoot, "workflow.json"), doc.ToJsonString());
            string results = Path.Combine(caseRoot, "results");
            int before = Count();
            CommandResult result = Command(Cli, ["workflow", "run", manifest, "--results-dir", results, "--no-history"],
                expected: 4,
                environment: new()
                { ["CMAKEWORKFLOW_TEST_FAILURE"] = failure, ["CMAKEWORKFLOW_TEST_NODE"] = "effect" });
            Check(result.Output.Contains("Injected checkpoint I/O failure", StringComparison.Ordinal), "Persistence failure was hidden");
            string journal = Directory.EnumerateFiles(results, "run.json", SearchOption.AllDirectories).Single();
            JsonObject report = J(File.ReadAllText(journal));
            Check(S(report["status"]) == "outcome_unknown" && Status(report, "effect") == "outcome_unknown"
                                                           && Count() == before,
                "Failed persistence allowed dispatch or silent success");
            _ = RunCli(1, "workflow", "resume", Path.GetDirectoryName(journal)!);
            Check(Count() == before, "Persistence failure allowed implicit retry");
            Pass($"durability failure at {failure} prevents dispatch and reports uncertainty");
        }
    }

    private static void CrashReconcile(List<string> args, string point, string directory)
    {
        string marker = Path.Combine(directory, point + ".audit-ready");
        ProcessStartInfo info = new(Cli)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["CMAKEWORKFLOW_TEST_FAULT"] = point;
        info.Environment["CMAKEWORKFLOW_TEST_NODE"] = "effect";
        info.Environment["CMAKEWORKFLOW_TEST_MARKER"] = marker;
        using Process process = Process.Start(info) ?? throw new UnreachableException("Cannot start reconciliation fault test");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!File.Exists(marker))
            {
                Check(!process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15),
                    "Reconciliation fault was not reached");
                Thread.Sleep(20);
            }

            process.Kill();
            Check(process.WaitForExit(10000), "Reconciliation crash timed out");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            Check(process.WaitForExit(10000) && Task.WaitAll([stdout, stderr], 5000),
                "Reconciliation cleanup timed out");
        }
    }
}