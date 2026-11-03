using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class ArtifactTests
{
    internal static void Run()
    {
        using var temp = new TempTree();
        var root = temp.Path;
        var source = Path.Combine(root, "source");
        CopyFixture("workflow", source);
        var build = Path.Combine(root, "build");
        Configure(source, build);
        var sourceHash = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(source, "first.cpp"))));
        JsonObject Document() => J("""
        {"schemaVersion":2,"contexts":{"tree":{"buildDirectory":"build","mode":"adopt",
          "provenancePolicy":"allowUnverified"}},"nodes":[
          {"id":"build-first","kind":"build","context":"tree","target":"first",
           "inputs":[{"path":"source/first.cpp","sha256":"SOURCE_HASH","snapshot":true}]},
          {"id":"produce","kind":"run","from":"build-first","arguments":["generate","1"],
           "outputs":[{"name":"header","path":"build/generated.hpp","fresh":true}]},
          {"id":"consume","kind":"build","context":"tree","target":"second",
           "inputs":[{"from":"produce","artifact":"header","path":"build/generated.hpp"}]}
        ]}
        """.Replace("SOURCE_HASH", sourceHash, StringComparison.Ordinal));

        var invalid = Document();
        invalid["nodes"]![0]!["inputs"]![0]!["sha256"] = "bad";
        Plan(root, invalid, 1);
        Check(!File.Exists(Path.Combine(build, "first worker")), "Invalid artifact declaration launched a build.");

        var unsupportedAffinity = Document();
        unsupportedAffinity["nodes"]![0]!["constraints"] = J("""{"cpuSet":"0"}""");
        Plan(root, unsupportedAffinity, 1);
        Check(!File.Exists(Path.Combine(build, "first worker")),
            "Unsupported build affinity declaration launched a build.");

        var changed = Document();
        File.AppendAllText(Path.Combine(source, "first.cpp"), "\n// changed\n");
        var (mutatedReport, _) = Workflow(root, changed, expected: 1, name: "input-mutated");
        Check(Status(mutatedReport, "build-first") == "failed"
            && Status(mutatedReport, "produce") == "blocked"
            && !File.Exists(Path.Combine(build, "first worker")), "Input mutation did not stop native build.");
        File.WriteAllText(Path.Combine(source, "first.cpp"), File.ReadAllText(
            Path.Combine(Root, "tests/fixtures/workflow/first.cpp")));

        var (report, runDirectory) = Workflow(root, Document(), name: "artifacts-ok");
        AllSucceeded(report);
        var producer = Item(report, "produce")["declaredArtifacts"]!.AsArray()
            .Single(x => S(x?["role"]) == "output")!;
        Check(S(producer["name"]) == "header" && S(producer["sha256"]).Length == 64,
            "Verified output evidence missing.");
        var input = Item(report, "build-first")["declaredArtifacts"]!.AsArray()
            .Single(x => S(x?["role"]) == "input")!;
        Check(File.Exists(S(input["snapshotPath"]))
            && S(input["sha256"]).Equals(sourceHash, StringComparison.OrdinalIgnoreCase),
            "Source snapshot or content identity missing.");
        Check(File.Exists(Path.Combine(runDirectory, "events.jsonl")), "Artifact journal missing.");
        var interrupted = Copy(report);
        interrupted["status"] = "outcome_unknown";
        Item(interrupted, "produce")["status"] = "outcome_unknown";
        var checkpoint = Path.Combine(runDirectory, "run.json");
        var bytes = System.Text.Encoding.UTF8.GetBytes(interrupted.ToJsonString());
        File.WriteAllBytes(checkpoint, bytes);
        var checkpointHash = Convert.ToHexString(SHA256.HashData(bytes));
        var reconcile = RunCli(1, "workflow", "reconcile", runDirectory,
            "--node", "produce", "--decision", "succeeded", "--reason", "operator saw output",
            "--expected-checkpoint", checkpointHash, "--confirm-stopped");
        Check(reconcile.Contains("cannot establish declared artifact", StringComparison.OrdinalIgnoreCase)
            && File.ReadAllBytes(checkpoint).SequenceEqual(bytes),
            "Manual success silently certified declared artifacts.");

        File.Delete(Path.Combine(build, "generated.hpp"));
        var missing = Document();
        missing["nodes"]![1]!["arguments"] = new JsonArray("args");
        var (missingReport, _) = Workflow(root, missing, expected: 1, name: "missing-output");
        Check(Status(missingReport, "produce") == "failed"
            && Status(missingReport, "consume") == "blocked", "Missing output was accepted.");

        File.WriteAllText(Path.Combine(build, "generated.hpp"), "stale");
        var (staleReport, _) = Workflow(root, Document(), expected: 1, name: "stale-output");
        Check(Status(staleReport, "produce") == "failed"
            && File.ReadAllText(Path.Combine(build, "generated.hpp")) == "stale",
            "Pre-existing fresh output was overwritten.");

        File.Delete(Path.Combine(build, "generated.hpp"));
        var tampered = Document();
        tampered["nodes"]!.AsArray().Insert(2, J("""
          {"id":"tamper","kind":"run","from":"build-first","dependsOn":["produce"],
           "arguments":["generate","2"]}
        """));
        tampered["nodes"]![3]!["dependsOn"] = new JsonArray("tamper");
        var (tamperedReport, _) = Workflow(root, tampered, expected: 1, name: "output-mutated");
        Check(Status(tamperedReport, "tamper") == "succeeded"
            && Status(tamperedReport, "consume") == "failed",
            "Producer output mutation did not block its consumer.");
        if (OperatingSystem.IsLinux())
        {
            var allowed = File.ReadLines("/proc/self/status").Single(x =>
                x.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)).Split(':', 2)[1].Trim();
            var firstCpu = allowed.Split(',')[0].Split('-')[0];
            var constrained = J("""
            {"schemaVersion":2,"contexts":{"tree":{"buildDirectory":"build","mode":"adopt",
              "provenancePolicy":"allowUnverified"}},"nodes":[
              {"id":"build","kind":"build","context":"tree","target":"first"},
              {"id":"run","kind":"run","from":"build","arguments":["affinity"],
               "constraints":{"cpuSet":"CPU"}}
            ]}
            """.Replace("CPU", firstCpu, StringComparison.Ordinal));
            var (affinityReport, affinityDirectory) = Workflow(root, constrained, name: "affinity-ok");
            AllSucceeded(affinityReport);
            Check(Log(affinityReport, affinityDirectory, "run").Contains("CPUS_ALLOWED=" + firstCpu),
                "Required CPU affinity was not applied before target execution.");
            Check(S(Item(affinityReport, "run")["executionConditions"]?["cpuSetRequested"]) == firstCpu,
                "Affinity admission evidence missing from journal.");
            var unavailable = Copy(constrained);
            unavailable["nodes"]![1]!["constraints"]!["cpuSet"] = "8191";
            var (unavailableReport, _) = Workflow(root, unavailable, expected: 1, name: "affinity-unavailable");
            Check(Status(unavailableReport, "run") == "failed"
                && S(Item(unavailableReport, "run")["error"]).Contains("outside host allowance"),
                "Unavailable required CPU affinity silently fell back.");
        }
        Pass("declared inputs, source snapshots, typed outputs, missing/stale/mutated artifact refusal");
    }
}
