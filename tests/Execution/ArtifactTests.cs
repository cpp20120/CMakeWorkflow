using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Execution;

internal static class ArtifactTests
{
    internal static void Run()
    {
        using TempTree temp = new();
        string root = temp.Path;
        string source = Path.Combine(root, "source");
        CopyFixture("workflow", source);
        string build = Path.Combine(root, "build");
        Configure(source, build);
        string sourceHash = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(source, "first.cpp"))));

        JsonObject Document() => J(/*lang=json,strict*/ """
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

        JsonObject invalid = Document();
        invalid["nodes"]![0]!["inputs"]![0]!["sha256"] = "bad";
        _ = Plan(root, invalid, 1);
        Check(!File.Exists(Path.Combine(build, "first worker")), "Invalid artifact declaration launched a build.");

        JsonObject unsupportedAffinity = Document();
        unsupportedAffinity["nodes"]![0]!["constraints"] = J(/*lang=json,strict*/ """{"cpuSet":"0"}""");
        _ = Plan(root, unsupportedAffinity, 1);
        Check(!File.Exists(Path.Combine(build, "first worker")),
            "Unsupported build affinity declaration launched a build.");

        JsonObject changed = Document();
        File.AppendAllText(Path.Combine(source, "first.cpp"), "\n// changed\n");
        (JsonObject? mutatedReport, string _) = Workflow(root, changed, expected: 1, name: "input-mutated");
        Check(Status(mutatedReport, "build-first") == "failed"
              && Status(mutatedReport, "produce") == "blocked"
              && !File.Exists(Path.Combine(build, "first worker")), "Input mutation did not stop native build.");
        File.WriteAllText(Path.Combine(source, "first.cpp"), File.ReadAllText(
            Path.Combine(Root, "tests/fixtures/workflow/first.cpp")));

        (JsonObject? report, string? runDirectory) = Workflow(root, Document(), name: "artifacts-ok");
        AllSucceeded(report);
        JsonNode producer = Item(report, "produce")["declaredArtifacts"]!.AsArray()
            .Single(x => S(x?["role"]) == "output")!;
        Check(S(producer["name"]) == "header" && S(producer["sha256"]).Length == 64,
            "Verified output evidence missing.");
        JsonNode input = Item(report, "build-first")["declaredArtifacts"]!.AsArray()
            .Single(x => S(x?["role"]) == "input")!;
        Check(File.Exists(S(input["snapshotPath"]))
              && S(input["sha256"]).Equals(sourceHash, StringComparison.OrdinalIgnoreCase),
            "Source snapshot or content identity missing.");
        Check(File.Exists(Path.Combine(runDirectory, "events.jsonl")), "Artifact journal missing.");
        JsonObject interrupted = Copy(report);
        interrupted["status"] = "outcome_unknown";
        Item(interrupted, "produce")["status"] = "outcome_unknown";
        string checkpoint = Path.Combine(runDirectory, "run.json");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(interrupted.ToJsonString());
        File.WriteAllBytes(checkpoint, bytes);
        string checkpointHash = Convert.ToHexString(SHA256.HashData(bytes));
        string reconcile = RunCli(1, "workflow", "reconcile", runDirectory,
            "--node", "produce", "--decision", "succeeded", "--reason", "operator saw output",
            "--expected-checkpoint", checkpointHash, "--confirm-stopped");
        Check(reconcile.Contains("cannot establish declared artifact", StringComparison.OrdinalIgnoreCase)
              && File.ReadAllBytes(checkpoint).SequenceEqual(bytes),
            "Manual success silently certified declared artifacts.");

        File.Delete(Path.Combine(build, "generated.hpp"));
        JsonObject missing = Document();
        missing["nodes"]![1]!["arguments"] = new JsonArray("args");
        (JsonObject? missingReport, string _) = Workflow(root, missing, expected: 1, name: "missing-output");
        Check(Status(missingReport, "produce") == "failed"
              && Status(missingReport, "consume") == "blocked", "Missing output was accepted.");

        File.WriteAllText(Path.Combine(build, "generated.hpp"), "stale");
        (JsonObject? staleReport, string _) = Workflow(root, Document(), expected: 1, name: "stale-output");
        Check(Status(staleReport, "produce") == "failed"
              && File.ReadAllText(Path.Combine(build, "generated.hpp")) == "stale",
            "Pre-existing fresh output was overwritten.");

        File.Delete(Path.Combine(build, "generated.hpp"));
        JsonObject tampered = Document();
        tampered["nodes"]!.AsArray().Insert(2, J(/*lang=json,strict*/ """
                                                   {"id":"tamper","kind":"run","from":"build-first","dependsOn":["produce"],
                                                    "arguments":["generate","2"]}
                                                 """));
        tampered["nodes"]![3]!["dependsOn"] = new JsonArray("tamper");
        (JsonObject? tamperedReport, string _) = Workflow(root, tampered, expected: 1, name: "output-mutated");
        Check(Status(tamperedReport, "tamper") == "succeeded"
              && Status(tamperedReport, "consume") == "failed",
            "Producer output mutation did not block its consumer.");
        if (OperatingSystem.IsLinux())
        {
            string allowed = File.ReadLines("/proc/self/status").Single(x =>
                x.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)).Split(':', 2)[1].Trim();
            string firstCpu = allowed.Split(',')[0].Split('-')[0];
            JsonObject constrained = J(/*lang=json,strict*/ """
                                {"schemaVersion":2,"contexts":{"tree":{"buildDirectory":"build","mode":"adopt",
                                  "provenancePolicy":"allowUnverified"}},"nodes":[
                                  {"id":"build","kind":"build","context":"tree","target":"first"},
                                  {"id":"run","kind":"run","from":"build","arguments":["affinity"],
                                   "constraints":{"cpuSet":"CPU"}}
                                ]}
                                """.Replace("CPU", firstCpu, StringComparison.Ordinal));
            (JsonObject? affinityReport, string? affinityDirectory) = Workflow(root, constrained, name: "affinity-ok");
            AllSucceeded(affinityReport);
            Check(Log(affinityReport, affinityDirectory, "run").Contains("CPUS_ALLOWED=" + firstCpu, StringComparison.Ordinal),
                "Required CPU affinity was not applied before target execution.");
            Check(S(Item(affinityReport, "run")["executionConditions"]?["cpuSetRequested"]) == firstCpu,
                "Affinity admission evidence missing from journal.");
            JsonObject unavailable = Copy(constrained);
            unavailable["nodes"]![1]!["constraints"]!["cpuSet"] = "8191";
            (JsonObject? unavailableReport, string _) = Workflow(root, unavailable, expected: 1, name: "affinity-unavailable");
            Check(Status(unavailableReport, "run") == "failed"
                  && S(Item(unavailableReport, "run")["error"]).Contains("outside host allowance", StringComparison.Ordinal),
                "Unavailable required CPU affinity silently fell back.");
        }

        Pass("declared inputs, source snapshots, typed outputs, missing/stale/mutated artifact refusal");
    }
}