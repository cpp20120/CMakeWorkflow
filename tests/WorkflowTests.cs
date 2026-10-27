using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class WorkflowTests
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);

    internal static void Run()
    {
        using var temp = new TempTree();
        var root = temp.Path;
        var source = Path.Combine(root, "source");
        CopyFixture("workflow", source);
        var build = Path.Combine(root, "worker space");
        Configure(source, build);
        var doc = J("""
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
        var firstExecutable = Path.Combine(build, OperatingSystem.IsWindows() ? "first worker.exe" : "first worker");
        var before = Fingerprint(build);
        Check(Plan(root, doc, 0, "--only", "run-one").Contains("Runtime"), "Plan omitted runtime validations");
        Check(Fingerprint(build) == before && !File.Exists(firstExecutable), "Plan mutated build tree");
        Pass("read-only dependency closure planning");

        var (report, output) = Workflow(root, doc, name: "interleaved", options: ["--jobs", "4"]);
        AllSucceeded(report);
        Check(Log(report, output, "run-one").Contains("VALUE=1") && Log(report, output, "run-two").Contains("VALUE=2"), "Build requests were hoisted or deduplicated");
        Check(Log(report, output, "action").Contains("EXISTING_ACTION"), "Invoke did not run custom target");
        Check(report["nodes"]!.AsArray().All(n => S(n?["configuration"]) == "Debug"), "Configuration was lost");
        Check(report["nodes"]!.AsArray().Where(n => S(n?["kind"]) == "run").All(n => S(n?["executableSha256"]).Length == 64), "Executable identity missing");
        Pass("interleaved generated inputs, repeated build/run, CTest and explicit invoke");

        Check(File.Exists(firstExecutable), "Initial native executable missing");
        var first = File.GetLastWriteTimeUtc(firstExecutable);
        var only = Copy(doc); only["nodes"] = new JsonArray(doc["nodes"]![0]!.DeepClone());
        (report, output) = Workflow(root, only, name: "incremental", options: ["--only", "build-first"]);
        AllSucceeded(report);
        Check(File.Exists(firstExecutable) && File.GetLastWriteTimeUtc(firstExecutable) == first
            && Log(report, output, "build-first").Contains("no work to do"), "Native no-op was not delegated to Ninja");

        var simple = Copy(only);
        simple["nodes"]!.AsArray().Add(J("""{"id":"run","kind":"run","from":"build-first","arguments":["args","hello world","","a;b","\"quoted\""],"environment":{"WORKFLOW_TEST_VALUE":"value with spaces"}}"""));
        (report, output) = Workflow(root, simple, name: "arguments");
        Check(Log(report, output, "run").Contains("[hello world][][a;b][\"quoted\"] ENV=value with spaces"), "Arguments/environment changed");
        var flood = Copy(simple); flood["nodes"]![1]!["arguments"] = new JsonArray("flood");
        (report, output) = Workflow(root, flood, name: "flood");
        Check(Log(report, output, "run").Split("stdout line").Length - 1 == 20000
            && Log(report, output, "run", "stderr").Split("stderr line").Length - 1 == 20000, "Subprocess output was truncated/deadlocked");
        Pass("native no-op, argument fidelity and concurrent process stream capture");

        var failed = Copy(simple); failed["nodes"]![1]!["arguments"] = new JsonArray("fail");
        failed["nodes"]!.AsArray().Add(J("""{"id":"blocked","kind":"invoke","context":"worker","target":"existing_action","dependsOn":["run"]}"""));
        failed["nodes"]!.AsArray().Add(J("""{"id":"independent","kind":"invoke","context":"worker","target":"existing_action"}"""));
        (report, output) = Workflow(root, failed, expected: 1, name: "failure", options: ["--jobs", "4"]);
        Check(Status(report, "run") == "failed" && Status(report, "blocked") == "blocked"
            && Status(report, "independent") == "succeeded" && S(Item(report, "run")["exitCode"]) == "7"
            && Log(report, output, "run", "stderr").Contains("EXPECTED_FAILURE"), "Failure propagation regressed");
        var brokenSource = Path.Combine(source, "first.cpp");
        var original = File.ReadAllText(brokenSource);
        try
        {
            File.WriteAllText(brokenSource, "#error EXPECTED_BUILD_FAILURE\n");
            (report, _) = Workflow(root, simple, expected: 1, name: "build-failure");
            Check(Status(report, "build-first") == "failed" && Status(report, "run") == "blocked", "Build failure did not block run");
        }
        finally { File.WriteAllText(brokenSource, original); }
        var invalid = Copy(simple); invalid["nodes"]![0]!["target"] = "missing";
        Check(Plan(root, invalid, 1).Contains("absent"), "Missing target accepted");
        invalid = Copy(simple); invalid["nodes"]![0]!["dependsOn"] = new JsonArray("run");
        Check(Plan(root, invalid, 1).Contains("cycle"), "Cycle accepted");
        invalid = Copy(simple); invalid["nodes"]![1]!["from"] = "missing";
        Check(Plan(root, invalid, 1).Contains("from must refer"), "Invalid run source accepted");
        invalid = Copy(simple); invalid["nodes"]![1]!["argumnts"] = new JsonArray();
        Check(Plan(root, invalid, 1).Contains("argumnts"), "Unknown schema field accepted");
        invalid = Copy(simple); invalid["nodes"]!.AsArray().Add(invalid["nodes"]![0]!.DeepClone());
        Check(Plan(root, invalid, 1).Contains("unique"), "Duplicate node ID accepted");
        invalid = J("""{"schemaVersion":2,"contexts":{"worker":{"buildDirectory":"worker space","mode":"adopt"}},"nodes":[{"id":"unbuilt","kind":"ctest","context":"worker"}]}""");
        Check(Plan(root, invalid, 1).Contains("build ancestor"), "CTest without preparation accepted");
        Check(Plan(root, simple, 1, "--only", "missing").Contains("Unknown selected node"), "Invalid selection accepted");
        Check(Plan(root, simple, 1, "--controller-build-dir", build).Contains("Unsafe nested build"), "Controller guard failed");
        var noTests = J("""{"schemaVersion":2,"contexts":{"worker":{"buildDirectory":"worker space","mode":"adopt"}},"nodes":[{"id":"build","kind":"build","context":"worker","target":"second"},{"id":"empty","kind":"ctest","context":"worker","dependsOn":["build"],"testRegex":"does-not-exist"}]}""");
        (report, _) = Workflow(root, noTests, expected: 1, name: "empty-ctest");
        Check(Status(report, "empty") == "failed", "Empty CTest selection succeeded");
        Pass("failure propagation, backend failure and graph validation");

        var multi = Path.Combine(root, "multi"); Configure(source, multi, multi: true);
        var multiDoc = Copy(simple); multiDoc["contexts"]!["worker"]!["buildDirectory"] = "multi";
        Check(Plan(root, multiDoc, 1).Contains("Multi-config build"), "Ambiguous multi-config accepted");
        multiDoc["contexts"]!["worker"]!["configuration"] = "Release";
        (report, _) = Workflow(root, multiDoc, name: "multi"); AllSucceeded(report);
        Check(File.Exists(Path.Combine(multi, "Release", Path.GetFileName(firstExecutable))),
            "Multi-config artifact missing");

        var bridge = Path.Combine(root, "bridge");
        Configure(Path.Combine(Root, "tests", "fixture"), bridge, bridge: true);
        Check(Command("cmake", ["--build", bridge, "--target", "fixture_bridge_worker"]).Output.Contains("fixture executable:"), "Prepared bridge failed");
        Check(RunCli("workflow", "sequence", "--build-dir", build, "--first", "first", "--second", "second").Contains("sequence completed"), "Legacy sequence failed");
        Pass("multi-config resolution, prepared bridge and legacy sequence");

        var timeout = Copy(simple); timeout["nodes"]![1]!["arguments"] = new JsonArray("sleep", "30000");
        timeout["nodes"]![1]!["timeoutSeconds"] = 0.3;
        (report, _) = Workflow(root, timeout, expected: 1, name: "timeout");
        Check(Status(report, "run") == "failed" && S(Item(report, "run")["error"]).Contains("Timed out"), "Timeout did not stop execution");
        if (OperatingSystem.IsLinux())
            Check(!Directory.Exists(Path.Combine("/proc", File.ReadAllText(Path.Combine(build, "ready.pid")))), "Timed-out worker survived");
        Pass("timeout and process-tree cleanup");

        var parallel = Copy(simple);
        parallel["contexts"]!["other"] = J("""{"buildDirectory":"multi","configuration":"Release","mode":"adopt"}""");
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
        var one = Item(report, "sleep-one"); var two = Item(report, "sleep-two");
        Check(S(one["startedAt"]).CompareTo(S(two["finishedAt"])) < 0
            && S(two["startedAt"]).CompareTo(S(one["finishedAt"])) < 0
            && S(Item(report, "benchmark")["startedAt"]).CompareTo(S(two["finishedAt"])) >= 0,
            "Parallel/exclusive scheduling failed");
        foreach (var node in parallel["nodes"]!.AsArray())
            if (S(node?["kind"]) == "run") node!["resources"] = new JsonArray("shared-device");
        (report, _) = Workflow(root, parallel, name: "resources", options: ["--jobs", "4"]);
        one = Item(report, "sleep-one"); two = Item(report, "sleep-two");
        Check(S(one["finishedAt"]).CompareTo(S(two["startedAt"])) <= 0
            || S(two["finishedAt"]).CompareTo(S(one["startedAt"])) <= 0, "Resource constraint failed");
        Pass("parallel scheduling, exclusive nodes and resource ownership");

        if (!OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(root, "worker-alias");
            Directory.CreateSymbolicLink(alias, build);
            Check(Plan(root, simple, 1, "--controller-build-dir", alias).Contains("Unsafe nested build"), "Controller alias bypassed guard");
            var aliased = Copy(parallel);
            aliased["contexts"]!["other"] = J("""{"buildDirectory":"worker-alias","mode":"adopt"}""");
            foreach (var node in aliased["nodes"]!.AsArray()) node!.AsObject().Remove("resources");
            (report, _) = Workflow(root, aliased, name: "same-tree", options: ["--jobs", "4"]);
            one = Item(report, "sleep-one"); two = Item(report, "sleep-two");
            Check(S(one["finishedAt"]).CompareTo(S(two["startedAt"])) <= 0
                || S(two["finishedAt"]).CompareTo(S(one["startedAt"])) <= 0, "Physical path aliases bypassed tree serialization");
            Directory.Delete(alias);
            Pass("physical build-tree alias serialization");

            var waiting = Copy(simple);
            waiting["nodes"]![1]!["arguments"] = new JsonArray("sleep", "30000");
            waiting["nodes"]!.AsArray().Add(J("""{"id":"after","kind":"invoke","context":"worker","target":"existing_action","dependsOn":["run"]}"""));
            var manifest = Write(Path.Combine(root, "waiting.json"), waiting.ToJsonString());
            var results = Path.Combine(root, "cancel-results");
            var marker = Path.Combine(build, "ready.pid");
            File.Delete(marker);
            Process Start(string[] arguments)
            {
                var info = new ProcessStartInfo(Cli) { UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in arguments) info.ArgumentList.Add(arg);
                return Process.Start(info) ?? throw new Exception("Cannot start CLI process");
            }
            using var primary = Start(["workflow", "run", manifest, "--results-dir", results]);
            Process? contender = null;
            var primaryOutput = primary.StandardOutput.ReadToEndAsync();
            var primaryError = primary.StandardError.ReadToEndAsync();
            Task<string>? contenderOutput = null;
            Task<string>? contenderError = null;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(marker))
                {
                    Check(!primary.HasExited && DateTime.UtcNow < deadline, "Sleeping worker did not start");
                    Thread.Sleep(30);
                }
                contender = Start(["workflow", "run", manifest, "--only", "build-first",
                    "--results-dir", Path.Combine(root, "contender-results")]);
                contenderOutput = contender.StandardOutput.ReadToEndAsync();
                contenderError = contender.StandardError.ReadToEndAsync();
                Thread.Sleep(400);
                Check(!contender.HasExited, "Second CLI bypassed build-tree lease");
                Check(kill(primary.Id, 2) == 0 && primary.WaitForExit(10000) && primary.ExitCode == 130,
                    "SIGINT did not cancel first CLI");
                Check(contender.WaitForExit(15000) && contender.ExitCode == 0,
                    "Contender did not proceed after cancellation");
                var journal = Directory.EnumerateFiles(results, "run.json", SearchOption.AllDirectories).Single();
                var canceled = J(File.ReadAllText(journal));
                Check(S(canceled["status"]) == "canceled" && Status(canceled, "after") == "canceled", "Cancellation journal incorrect");
                Pass("cross-process lease, console cancellation and dependent propagation");
            }
            finally
            {
                foreach (var process in new Process?[] { primary, contender })
                {
                    if (process is null) continue;
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    if (process != primary) process.Dispose();
                }
                _ = primaryOutput.Result + primaryError.Result + (contenderOutput?.Result ?? "") + (contenderError?.Result ?? "");
            }
        }
    }
}
