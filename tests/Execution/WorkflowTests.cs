using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests.Execution;
#pragma warning restore IDE0130

internal static class WorkflowTests
{
    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kill(int pid, int signal);

    internal static void Run()
    {
        using TempTree temp = new();
        string root = temp.Path;
        string source = Path.Combine(root, "source");
        CopyFixture("workflow", source);
        string build = Path.Combine(root, "worker space");
        Configure(source, build);
        JsonObject doc = J(/*lang=json,strict*/ """
                    {"schemaVersion":1,"contexts":{"worker":{"buildDirectory":"worker space","mode":"adopt"}},"nodes":[
                      {"id":"build-first","kind":"build","context":"worker","target":"first"},
                      {"id":"generate-one","kind":"run","from":"build-first","arguments":["generate","1"]},
                      {"id":"build-second","kind":"build","context":"worker","target":"second","dependsOn":["generate-one"]},
                      {"id":"run-one","kind":"run","from":"build-second"},
                      {"id":"generate-two","kind":"run","from":"build-first","dependsOn":["run-one"],"arguments":["generate","2"]},
                      {"id":"rebuild-second","kind":"build","context":"worker","target":"second","dependsOn":["generate-two"]},
                      {"id":"run-two","kind":"run","from":"rebuild-second","exclusive":true},
                      {"id":"check","kind":"ctest","context":"worker","dependsOn":["run-two"],"labelRegex":"smoke"},
                      {"id":"action","kind":"invoke","context":"worker","target":"existing_action","dependsOn":["check"]}
                    ]}
                    """);
        string firstExecutable = Path.Combine(build, OperatingSystem.IsWindows() ? "first worker.exe" : "first worker");
        string before = Fingerprint(build);
        Check(Plan(root, doc, 0, "--only", "run-one").Contains("Runtime", StringComparison.Ordinal), "Plan omitted runtime validations");
        Check(Fingerprint(build) == before && !File.Exists(firstExecutable), "Plan mutated build tree");
        Pass("read-only dependency closure planning");

        (JsonObject? report, string? output) = Workflow(root, doc, name: "interleaved", options: ["--jobs", "4"]);
        AllSucceeded(report);
        Check(Log(report, output, "run-one").Contains("VALUE=1", StringComparison.Ordinal) && Log(report, output, "run-two").Contains("VALUE=2", StringComparison.Ordinal),
            "Build requests were hoisted or deduplicated");
        Check(Log(report, output, "action").Contains("EXISTING_ACTION", StringComparison.Ordinal), "Invoke did not run custom target");
        Check(report["nodes"]!.AsArray().All(n => S(n?["configuration"]) == "Debug"), "Configuration was lost");
        Check(
            report["nodes"]!.AsArray().Where(n => S(n?["kind"]) == "run")
                .All(n => S(n?["executableSha256"]).Length == 64), "Executable identity missing");
        Pass("interleaved generated inputs, repeated build/run, CTest and explicit invoke");

        // Adapted from outcome-unknown-tests: observe the real invoke target's
        // append-only output, not an unrelated marker that a replay cannot touch.
        string actionMarker = Path.Combine(build, "action-invocations.txt");
        Check(File.ReadAllText(actionMarker).ReplaceLineEndings("\n") == "invoked\n",
            "Invoke marker is not connected to the fixture target");
        string interruptedManifest = Path.Combine(root, "interleaved.json");
        string savedBuildFingerprint = Fingerprint(build);
        foreach (string? scenario in new[] { "running", "needs_attention", "phase-only", "admission-only" })
        {
            string interrupted = Path.Combine(root, "interrupted-" + scenario);
            JsonObject interruptedReport = Copy(report);
            interruptedReport["status"] = scenario is "running" or "admission-only" ? "running" : "needs_attention";
            interruptedReport["recoveryPhase"] = scenario == "admission-only" ? null : "running";
            interruptedReport["resumeCount"] = 7;
            interruptedReport["finishedAt"] = null;
            if (scenario != "phase-only")
            {
                JsonObject node = Item(interruptedReport, "action");
                node["status"] = scenario == "admission-only" ? "pending" : "running";
                node["finishedAt"] = null;
            }

            string file = Write(Path.Combine(interrupted, "run.json"), interruptedReport.ToJsonString());
            _ = RunCli(4, "workflow", "run", interruptedManifest, "--resume-dir", interrupted);
            string checkpoint = File.ReadAllText(file);
            JsonObject quarantined = J(checkpoint);
            Check(S(quarantined["status"]) == "outcome_unknown"
                  && S(quarantined["recoveryPhase"]) == "interrupted"
                  && S(quarantined["resumeCount"]) == "7"
                  && string.IsNullOrEmpty(S(quarantined["finishedAt"])),
                "Interrupted run was replayed or given a fabricated completion");
            Check(Status(quarantined, "action") == (scenario == "phase-only" ? "succeeded"
                : scenario == "admission-only" ? "pending" : "outcome_unknown"), "Incorrect quarantine node state");
            Check(Status(quarantined, "build-first") == "succeeded", "Quarantine changed successful nodes");
            _ = RunCli(1, "workflow", "resume", interrupted);
            _ = RunCli(1, "workflow", "run", interruptedManifest, "--resume-dir", interrupted);
            Check(File.ReadAllText(file) == checkpoint && !File.Exists(file + ".tmp"),
                "Repeated resume changed quarantined checkpoint");
            Check(
                File.ReadAllText(actionMarker).ReplaceLineEndings("\n") == "invoked\n" &&
                Fingerprint(build) == savedBuildFingerprint,
                "Unknown-outcome resume changed external state");
            string rendered = RunCli("workflow", "report", interrupted, "--json");
            Check(S(J(rendered)["status"]) == "outcome_unknown", "Unknown-outcome report is not readable");
        }

        Pass("interrupted admission/recovery quarantine, unchanged side effects and repeated resume rejection");

        Check(File.Exists(firstExecutable), "Initial native executable missing");
        DateTime first = File.GetLastWriteTimeUtc(firstExecutable);
        JsonObject only = Copy(doc);
        only["nodes"] = new JsonArray(doc["nodes"]![0]!.DeepClone());
        (report, output) = Workflow(root, only, name: "incremental", options: ["--only", "build-first"]);
        AllSucceeded(report);
        Check(File.Exists(firstExecutable) && File.GetLastWriteTimeUtc(firstExecutable) == first
                                           && Log(report, output, "build-first").Contains("no work to do", StringComparison.Ordinal),
            "Native no-op was not delegated to Ninja");

        JsonObject simple = Copy(only);
        simple["nodes"]!.AsArray()
            .Add(J(
                                     /*lang=json,strict*/
                                     """{"id":"run","kind":"run","from":"build-first","arguments":["args","hello world","","a;b","\"quoted\""],"environment":{"WORKFLOW_TEST_VALUE":"value with spaces"}}"""));
        (report, output) = Workflow(root, simple, name: "arguments");
        Check(Log(report, output, "run").Contains("[hello world][][a;b][\"quoted\"] ENV=value with spaces", StringComparison.Ordinal),
            "Arguments/environment changed");
        JsonObject flood = Copy(simple);
        flood["nodes"]![1]!["arguments"] = new JsonArray("flood");
        (report, output) = Workflow(root, flood, name: "flood");
        Check(Log(report, output, "run").Split("stdout line").Length - 1 == 20000
              && Log(report, output, "run", "stderr").Split("stderr line").Length - 1 == 20000,
            "Subprocess output was truncated/deadlocked");
        Pass("native no-op, argument fidelity and concurrent process stream capture");

        JsonObject failed = Copy(simple);
        failed["nodes"]![1]!["arguments"] = new JsonArray("fail");
        failed["nodes"]!.AsArray()
            .Add(J(
                                     /*lang=json,strict*/
                                     """{"id":"blocked","kind":"invoke","context":"worker","target":"existing_action","dependsOn":["run"]}"""));
        failed["nodes"]!.AsArray()
            .Add(J(/*lang=json,strict*/ """{"id":"independent","kind":"invoke","context":"worker","target":"existing_action"}"""));
        (report, output) = Workflow(root, failed, expected: 1, name: "failure", options: ["--jobs", "4"]);
        Check(Status(report, "run") == "failed" && Status(report, "blocked") == "blocked"
                                                && Status(report, "independent") == "succeeded" &&
                                                S(Item(report, "run")["exitCode"]) == "7"
                                                && Log(report, output, "run", "stderr").Contains("EXPECTED_FAILURE", StringComparison.Ordinal),
            "Failure propagation regressed");
        string brokenSource = Path.Combine(source, "first.cpp");
        string original = File.ReadAllText(brokenSource);
        try
        {
            File.WriteAllText(brokenSource, "#error EXPECTED_BUILD_FAILURE\n");
            (report, _) = Workflow(root, simple, expected: 1, name: "build-failure");
            Check(Status(report, "build-first") == "failed" && Status(report, "run") == "blocked",
                "Build failure did not block run");
        }
        finally
        {
            File.WriteAllText(brokenSource, original);
        }

        JsonObject invalid = Copy(simple);
        invalid["nodes"]![0]!["target"] = "missing";
        Check(Plan(root, invalid, 1).Contains("absent", StringComparison.Ordinal), "Missing target accepted");
        invalid = Copy(simple);
        invalid["nodes"]![0]!["dependsOn"] = new JsonArray("run");
        Check(Plan(root, invalid, 1).Contains("cycle", StringComparison.Ordinal), "Cycle accepted");
        invalid = Copy(simple);
        invalid["nodes"]![1]!["from"] = "missing";
        Check(Plan(root, invalid, 1).Contains("from must refer", StringComparison.Ordinal), "Invalid run source accepted");
        invalid = Copy(simple);
        invalid["nodes"]![1]!["argumnts"] = new JsonArray();
        Check(Plan(root, invalid, 1).Contains("argumnts", StringComparison.Ordinal), "Unknown schema field accepted");
        invalid = Copy(simple);
        invalid["nodes"]!.AsArray().Add(invalid["nodes"]![0]!.DeepClone());
        Check(Plan(root, invalid, 1).Contains("unique", StringComparison.Ordinal), "Duplicate node ID accepted");
        invalid = J(
                                 /*lang=json,strict*/
                                 """{"schemaVersion":2,"contexts":{"worker":{"buildDirectory":"worker space","mode":"adopt"}},"nodes":[{"id":"unbuilt","kind":"ctest","context":"worker"}]}""");
        Check(Plan(root, invalid, 1).Contains("build ancestor", StringComparison.Ordinal), "CTest without preparation accepted");
        Check(Plan(root, simple, 1, "--only", "missing").Contains("Unknown selected node", StringComparison.Ordinal),
            "Invalid selection accepted");
        Check(Plan(root, simple, 1, "--controller-build-dir", build).Contains("Unsafe nested build", StringComparison.Ordinal),
            "Controller guard failed");
        JsonObject noTests =
            J(
                                     /*lang=json,strict*/
                                     """{"schemaVersion":2,"contexts":{"worker":{"buildDirectory":"worker space","mode":"adopt"}},"nodes":[{"id":"build","kind":"build","context":"worker","target":"second"},{"id":"empty","kind":"ctest","context":"worker","dependsOn":["build"],"testRegex":"does-not-exist"}]}""");
        (report, _) = Workflow(root, noTests, expected: 1, name: "empty-ctest");
        Check(Status(report, "empty") == "failed", "Empty CTest selection succeeded");
        Pass("failure propagation, backend failure and graph validation");

        string multi = Path.Combine(root, "multi");
        Configure(source, multi, multi: true);
        JsonObject multiDoc = Copy(simple);
        multiDoc["contexts"]!["worker"]!["buildDirectory"] = "multi";
        Check(Plan(root, multiDoc, 1).Contains("Multi-config build", StringComparison.Ordinal), "Ambiguous multi-config accepted");
        multiDoc["contexts"]!["worker"]!["configuration"] = "Release";
        (report, _) = Workflow(root, multiDoc, name: "multi");
        AllSucceeded(report);
        Check(File.Exists(Path.Combine(multi, "Release", Path.GetFileName(firstExecutable))),
            "Multi-config artifact missing");

        string bridge = Path.Combine(root, "bridge");
        Configure(Path.Combine(Root, "tests", "fixture"), bridge, bridge: true);
        Check(
            Command("cmake", ["--build", bridge, "--target", "fixture_bridge_worker"]).Output
                .Contains("fixture executable:", StringComparison.Ordinal), "Prepared bridge failed");
        Check(
            RunCli("workflow", "sequence", "--build-dir", build, "--first", "first", "--second", "second")
                .Contains("sequence completed", StringComparison.Ordinal), "Legacy sequence failed");
        Pass("multi-config resolution, prepared bridge and legacy sequence");

        JsonObject timeout = Copy(simple);
        timeout["nodes"]![1]!["arguments"] = new JsonArray("sleep", "30000");
        timeout["nodes"]![1]!["timeoutSeconds"] = 0.3;
        (report, _) = Workflow(root, timeout, expected: 1, name: "timeout");
        // Report the actual terminal state and Win32 cleanup error instead of
        // losing the only evidence in a generic assertion failure on CI.
        JsonObject timedOutRun = Item(report, "run");
        Check(Status(report, "run") == "failed" && S(timedOutRun["error"]).Contains("Timed out", StringComparison.Ordinal),
            "Timeout did not stop execution: " + timedOutRun.ToJsonString());
        if (OperatingSystem.IsLinux())
        {
            Check(!Directory.Exists(Path.Combine("/proc", File.ReadAllText(Path.Combine(build, "ready.pid")))),
                "Timed-out worker survived");
        }

        Pass("timeout and process-tree cleanup");

        JsonObject parallel = Copy(simple);
        parallel["contexts"]!["other"] = J(/*lang=json,strict*/ """{"buildDirectory":"multi","configuration":"Release","mode":"adopt"}""");
        parallel["nodes"] = JsonNode.Parse("""
                                           [
                                             {"id":"build-first","kind":"build","context":"worker","target":"first"},
                                             {"id":"build-other","kind":"build","context":"other","target":"first"},
                                             {"id":"sleep-one","kind":"run","from":"build-first","dependsOn":["build-other"],"arguments":["sleep","200"]},
                                             {"id":"sleep-two","kind":"run","from":"build-other","dependsOn":["build-first"],"arguments":["sleep","600"]},
                                             {"id":"benchmark","kind":"run","from":"build-first","dependsOn":["sleep-one"],"arguments":["args"],"exclusive":true}
                                           ]
                                           """);
        (report, _) = Workflow(root, parallel, name: "parallel", options: ["--jobs", "4"]);
        JsonObject one = Item(report, "sleep-one");
        JsonObject two = Item(report, "sleep-two");
        Check(string.Compare(S(one["startedAt"]), S(two["finishedAt"]), StringComparison.Ordinal) < 0
              && string.Compare(S(two["startedAt"]), S(one["finishedAt"]), StringComparison.Ordinal) < 0
              && string.Compare(S(Item(report, "benchmark")["startedAt"]), S(two["finishedAt"]), StringComparison.Ordinal) >= 0,
            "Parallel/exclusive scheduling failed");
        foreach (JsonNode? node in parallel["nodes"]!.AsArray())
        {
            if (S(node?["kind"]) == "run")
            {
                node!["resources"] = new JsonArray("shared-device");
            }
        }

        (report, _) = Workflow(root, parallel, name: "resources", options: ["--jobs", "4"]);
        one = Item(report, "sleep-one");
        two = Item(report, "sleep-two");
        Check(string.Compare(S(one["finishedAt"]), S(two["startedAt"]), StringComparison.Ordinal) <= 0
              || string.Compare(S(two["finishedAt"]), S(one["startedAt"]), StringComparison.Ordinal) <= 0, "Resource constraint failed");
        Pass("parallel scheduling, exclusive nodes and resource ownership");

        if (!OperatingSystem.IsWindows())
        {
            string alias = Path.Combine(root, "worker-alias");
            _ = Directory.CreateSymbolicLink(alias, build);
            Check(Plan(root, simple, 1, "--controller-build-dir", alias).Contains("Unsafe nested build", StringComparison.Ordinal),
                "Controller alias bypassed guard");
            JsonObject aliased = Copy(parallel);
            aliased["contexts"]!["other"] = J(/*lang=json,strict*/ """{"buildDirectory":"worker-alias","mode":"adopt"}""");
            foreach (JsonNode? node in aliased["nodes"]!.AsArray())
            {
                _ = node!.AsObject().Remove("resources");
            }

            (report, _) = Workflow(root, aliased, name: "same-tree", options: ["--jobs", "4"]);
            one = Item(report, "sleep-one");
            two = Item(report, "sleep-two");
            Check(string.Compare(S(one["finishedAt"]), S(two["startedAt"]), StringComparison.Ordinal) <= 0
                  || string.Compare(S(two["finishedAt"]), S(one["startedAt"]), StringComparison.Ordinal) <= 0,
                "Physical path aliases bypassed tree serialization");
            Directory.Delete(alias);
            Pass("physical build-tree alias serialization");

            JsonObject waiting = Copy(simple);
            waiting["nodes"]![1]!["arguments"] = new JsonArray("sleep", "30000");
            waiting["nodes"]!.AsArray()
                .Add(J(
                                         /*lang=json,strict*/
                                         """{"id":"after","kind":"invoke","context":"worker","target":"existing_action","dependsOn":["run"]}"""));
            string manifest = Write(Path.Combine(root, "waiting.json"), waiting.ToJsonString());
            string results = Path.Combine(root, "cancel-results");
            string marker = Path.Combine(build, "ready.pid");
            File.Delete(marker);

            static Process Start(string[] arguments)
            {
                ProcessStartInfo info = new(Cli)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (string arg in arguments)
                {
                    info.ArgumentList.Add(arg);
                }

                return Process.Start(info) ?? throw new UnreachableException("Cannot start CLI process");
            }

            using Process primary = Start(["workflow", "run", manifest, "--results-dir", results]);
            Process? contender = null;
            Task<string> primaryOutput = primary.StandardOutput.ReadToEndAsync();
            Task<string> primaryError = primary.StandardError.ReadToEndAsync();
            Task<string>? contenderOutput = null;
            Task<string>? contenderError = null;
            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(marker))
                {
                    Check(!primary.HasExited && DateTime.UtcNow < deadline, "Sleeping worker did not start");
                    Thread.Sleep(30);
                }

                contender = Start([
                    "workflow", "run", manifest, "--only", "build-first",
                    "--results-dir", Path.Combine(root, "contender-results")
                ]);
                contenderOutput = contender.StandardOutput.ReadToEndAsync();
                contenderError = contender.StandardError.ReadToEndAsync();
                Thread.Sleep(400);
                Check(!contender.HasExited, "Second CLI bypassed build-tree lease");
                Check(kill(primary.Id, 2) == 0 && primary.WaitForExit(10000) && primary.ExitCode == 130,
                    "SIGINT did not cancel first CLI");
                Check(contender.WaitForExit(15000) && contender.ExitCode == 0,
                    "Contender did not proceed after cancellation");
                string journal = Directory.EnumerateFiles(results, "run.json", SearchOption.AllDirectories).Single();
                JsonObject canceled = J(File.ReadAllText(journal));
                Check(S(canceled["status"]) == "canceled" && Status(canceled, "after") == "canceled",
                    "Cancellation journal incorrect");
                Pass("cross-process lease, console cancellation and dependent propagation");
            }
            finally
            {
                foreach (Process? process in new Process?[] { primary, contender })
                {
                    if (process is null)
                    {
                        continue;
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    process.WaitForExit();
                    if (process != primary)
                    {
                        process.Dispose();
                    }
                }

                _ = primaryOutput.Result + primaryError.Result + (contenderOutput?.Result ?? "") +
                    (contenderError?.Result ?? "");
            }

            string crashResults = Path.Combine(root, "crash-results");
            File.Delete(marker);
            using Process doomed = Start(["workflow", "run", manifest, "--results-dir", crashResults]);
            Task<string> doomedOutput = doomed.StandardOutput.ReadToEndAsync();
            Task<string> doomedError = doomed.StandardError.ReadToEndAsync();
            Process? child = null;
            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                int childPid;
                while (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker).Trim(), out childPid))
                {
                    Check(!doomed.HasExited && DateTime.UtcNow < deadline, "Crash-test child did not start");
                    Thread.Sleep(30);
                }

                child = Process.GetProcessById(childPid);
                string journal = Directory.EnumerateFiles(crashResults, "run.json", SearchOption.AllDirectories).Single();
                string runDirectory = Path.GetDirectoryName(journal)!;
                string admitted = File.ReadAllText(journal);
                Check(Status(J(admitted), "run") == "running", "External operation has no write-ahead checkpoint");
                // A live run cannot be mistaken for a crashed one by another runner.
                _ = RunCli(1, "workflow", "resume", runDirectory);
                Check(File.ReadAllText(journal) == admitted, "Concurrent resume mutated a live checkpoint");

                doomed.Kill(); // SIGKILL only the runner. This is not graceful cancellation.
                Check(doomed.WaitForExit(10000), "Runner did not exit after SIGKILL");
                // SIGKILL cannot promise process-tree cleanup. The test owns and
                // explicitly stops the child before checking for unwanted replay.
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }

                Check(child.WaitForExit(5000), "Crash-test child did not stop");
                string invocations = File.ReadAllText(Path.Combine(build, "sleep-invocations.txt"));
                string fingerprint = Fingerprint(build);
                _ = RunCli(4, "workflow", "resume", runDirectory);
                string checkpoint = File.ReadAllText(journal);
                JsonObject quarantined = J(checkpoint);
                Check(S(quarantined["status"]) == "outcome_unknown"
                      && Status(quarantined, "run") == "outcome_unknown"
                      && Status(quarantined, "build-first") == "succeeded"
                      && Status(quarantined, "after") == "pending"
                      && string.IsNullOrEmpty(S(Item(quarantined, "run")["finishedAt"])),
                    "SIGKILL quarantine fabricated completion or changed other nodes");
                _ = RunCli(1, "workflow", "run", manifest, "--resume-dir", runDirectory);
                Check(File.ReadAllText(journal) == checkpoint && Fingerprint(build) == fingerprint
                                                              && File.ReadAllText(Path.Combine(build,
                                                                  "sleep-invocations.txt")) == invocations,
                    "Resume replayed an interrupted external operation");
                Pass("SIGKILL quarantine, live-run lease and no replay of actual side effects");
            }
            finally
            {
                if (!doomed.HasExited)
                {
                    doomed.Kill(entireProcessTree: true);
                }

                Check(doomed.WaitForExit(10000), "Crash-test runner cleanup timed out");
                if (child is not null)
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }

                    Check(child.WaitForExit(5000), "Crash-test child cleanup timed out");
                    child.Dispose();
                }

                Check(Task.WaitAll([doomedOutput, doomedError], 5000), "Crash-test output pipes did not close");
            }
        }
    }
}