using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class BoundaryTests
{
    internal static void Run()
    {
        using var temp = new TempTree();
        var root = temp.Path;
        var source = Path.Combine(root, "source"); CopyFixture("boundary", source);
        var cmakeList = Path.Combine(source, "CMakeLists.txt");
        File.WriteAllText(cmakeList, "set(TEST_CLI \"" + Cli.Replace('\\', '/') + "\")\n" + File.ReadAllText(cmakeList));
        var build = Path.Combine(root, "managed");
        var workerExecutableName = OperatingSystem.IsWindows() ? "worker.exe" : "worker";
        var doc = J("""
        {"schemaVersion":2,"contexts":{"worker":{"buildDirectory":"managed","sourceDirectory":"source","configurePreset":"debug","configuration":"Debug"}},"nodes":[
          {"id":"configure","kind":"configure","context":"worker"},
          {"id":"build","kind":"build","context":"worker","target":"worker","dependsOn":["configure"]},
          {"id":"run","kind":"run","from":"build"},
          {"id":"test","kind":"ctest","context":"worker","dependsOn":["run"]}
        ]}
        """);
        var planFile = Write(Path.Combine(root, "initial-plan.json"), doc.ToJsonString());
        var before = Fingerprint(root);
        var plan = Command(Cli, ["workflow", "plan", planFile]).Output;
        Check(plan.Contains("Unknown") && plan.Contains("Runtime") && plan.Contains("Unverified")
            && Fingerprint(root) == before && !Directory.Exists(build), "Plan mutated an absent context");
        Check(S(Inspect(build)["state"]) == "Unknown", "Absent observation was guessed");
        var (report, output) = Workflow(root, doc, name: "managed"); AllSucceeded(report);
        Check(S(Item(report, "configure")["configure"]?["state"]) == "Succeeded"
            && S(Item(report, "build")["buildVerification"]?["verification"]) == "BackendSucceeded"
            && S(Item(report, "run")["artifact"]?["materialization"]) == "VerifiedAgainstBuild"
            && S(Item(report, "run")["experimentResult"]) == "succeeded", "Build/experiment facts were conflated");
        var observed = Inspect(build);
        Check(S(observed["freshness"]) == "Unverified" && S(observed["presetState"]) == "RecordedInvocation"
            && observed["hasCacheReply"]!.GetValue<bool>() && observed["toolchains"]!.AsArray().Count > 0
            && File.ReadAllText(Path.Combine(build, "preset-env.txt")) == "from-cmake-preset"
            && Log(report, output, "run").Contains("WORKER_RAN"), "Managed preset/environment observation failed");
        Pass("read-only plan and explicit managed configure through inherited CMake preset");

        var existing = Copy(doc);
        existing["nodes"]!.AsArray().RemoveAt(0);
        existing["nodes"]![0]!["dependsOn"] = new JsonArray();
        var mismatch = Copy(existing); mismatch["contexts"]!["worker"]!["configurePreset"] = "same-cache";
        Check(Plan(root, mismatch, 1).Contains("conflicts"), "Preset identity inferred from matching cache");
        mismatch = Copy(existing); mismatch["contexts"]!["worker"]!["configuration"] = "Release";
        Check(Plan(root, mismatch, 1).Contains("disagrees"), "Configuration mismatch accepted");
        Command("cmake", ["--preset", "same-cache", "-S", source, "-B", build]);
        Check(Plan(root, existing, 1).Contains("Unverified"), "External configure retained recorded provenance");
        var adopted = Copy(existing); adopted["contexts"]!["worker"]!["mode"] = "adopt";
        adopted["contexts"]!["worker"]!["provenancePolicy"] = "allowUnverified";
        (report, _) = Workflow(root, adopted, name: "adopt"); AllSucceeded(report);
        Check(S(Item(report, "build")["observedContext"]?["presetState"]) == "GenerationChanged", "Adoption certified preset identity");
        (report, _) = Workflow(root, doc, name: "restore"); AllSucceeded(report);
        Pass("preset identity, known conflicts and explicit adoption");

        var campaign = Copy(existing);
        campaign["nodes"] = JsonNode.Parse("""[{"id":"prepare","kind":"build","context":"worker","target":"fuzz_campaign"},{"id":"run","kind":"run","from":"prepare"}]""");
        Check(Plan(root, campaign, 1).Contains("invoke"), "UTILITY target accepted as build");
        var unsafeManifest = Write(Path.Combine(root, "unsafe.json"), campaign.ToJsonString());
        var unsafeResults = Path.Combine(root, "unsafe-results");
        Check(Command(Cli, ["workflow", "run", unsafeManifest, "--results-dir", unsafeResults], 1).Output.Contains("Preflight rejected")
            && !Directory.Exists(unsafeResults) && !File.Exists(Path.Combine(build, "CAMPAIGN_RAN")), "Unsafe campaign ran implicitly");
        var dangerous = Copy(campaign);
        dangerous["nodes"]!.AsArray().Add(J("""{"id":"independent-campaign","kind":"invoke","context":"worker","target":"fuzz_campaign"}"""));
        var dangerousManifest = Write(Path.Combine(root, "dangerous.json"), dangerous.ToJsonString());
        Check(Command(Cli, ["workflow", "run", dangerousManifest, "--results-dir", Path.Combine(root, "dangerous-results")], 1)
            .Output.Contains("Preflight rejected") && !File.Exists(Path.Combine(build, "CAMPAIGN_RAN")), "Independent campaign ran despite preflight error");
        foreach (var verb in new[] { "build", "run" })
            Check(RunCli(1, "target", verb, "--build-dir", build, "--target", "fuzz_campaign").Contains("UTILITY"), "Target CLI ran a campaign");
        var explicitDoc = Copy(campaign);
        explicitDoc["nodes"] = JsonNode.Parse("""[{"id":"campaign","kind":"invoke","context":"worker","target":"fuzz_campaign"}]""");
        (report, _) = Workflow(root, explicitDoc, name: "explicit-campaign");
        Check(Status(report, "campaign") == "succeeded" && File.Exists(Path.Combine(build, "CAMPAIGN_RAN"))
            && Item(report, "campaign")["buildVerification"] is null, "Invoke was mislabeled or did not execute");
        File.Delete(Path.Combine(build, "CAMPAIGN_RAN"));
        RunCli("target", "invoke", "--build-dir", build, "--target", "fuzz_campaign");
        Check(File.Exists(Path.Combine(build, "CAMPAIGN_RAN")), "Explicit target invoke did not run");
        RunCli("target", "build", "--build-dir", build, "--target", "native_library");
        Pass("native Build versus intentional Invoke and UTILITY safety");

        var limited = Path.Combine(root, "limited");
        Command("cmake", ["--preset", "debug", "-S", source, "-B", limited]);
        var limitedDoc = Copy(adopted); limitedDoc["contexts"]!["worker"] = J("""{"buildDirectory":"limited","configuration":"Debug","mode":"adopt"}""");
        Check(Plan(root, limitedDoc, 1).Contains("Unknown") && !File.Exists(Path.Combine(limited, workerExecutableName)), "Missing File API metadata caused build");
        Check(S(Inspect(limited)["state"]) == "ObservedLimited", "Limited observation misclassified");
        limitedDoc["contexts"]!["worker"]!["mode"] = "managed";
        limitedDoc["contexts"]!["worker"]!["sourceDirectory"] = "source";
        limitedDoc["contexts"]!["worker"]!["configurePreset"] = "debug";
        limitedDoc["nodes"]!.AsArray().Insert(0, doc["nodes"]![0]!.DeepClone());
        limitedDoc["nodes"]![1]!["dependsOn"] = new JsonArray("configure");
        (report, _) = Workflow(root, limitedDoc, name: "repair-file-api"); AllSucceeded(report);
        Pass("unknown native target metadata requires an explicit configure");

        var bridge = Path.Combine(root, "bridge");
        Command("cmake", ["-S", Path.Combine(Root, "tests", "fixture"), "-B", bridge, "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Debug", "-DWORKFLOW_RUNNER_EXECUTABLE=" + Cli.Replace('\\', '/')]);
        Command("cmake", ["--build", bridge, "--target", "fixture_bridge_worker"]);
        Check(Inspect(bridge)["targets"]!.AsArray().Any(t => S(t?["evidence"]) == "BridgeManifest"), "Bridge fallback was lost");
        var stale = Path.Combine(build, "cmake-workflow", "targets", "Debug", "fuzz_campaign.json");
        Write(stale, "{\"schemaVersion\":1,\"target\":\"fuzz_campaign\",\"configuration\":\"Debug\",\"executable\":\""
            + Path.Combine(build, workerExecutableName).Replace("\\", "\\\\") + "\"}");
        Check(RunCli(1, "target", "run", "--build-dir", build, "--target", "fuzz_campaign").Contains("UTILITY"),
            "Stale bridge manifest overrode current codemodel");
        Pass("bridge fallback and current codemodel priority");

        var failed = Copy(doc); failed["contexts"]!["worker"]!["configurePreset"] = "failure";
        (report, _) = Workflow(root, failed, expected: 1, name: "failed-configure");
        Check(Status(report, "configure") == "failed" && Status(report, "build") == "blocked"
            && S(Inspect(build)["state"]) == "ConfigureFailed", "Failed configure reused stale success");
        (report, _) = Workflow(root, doc, name: "recover"); AllSucceeded(report);
        var wrong = Copy(doc); wrong["contexts"]!["worker"]!["buildDirectory"] = "wrong-config";
        wrong["contexts"]!["worker"]!["configuration"] = "Release";
        (report, _) = Workflow(root, wrong, expected: 1, name: "wrong-config");
        Check(Status(report, "configure") == "failed" && Status(report, "build") == "blocked", "Requested configuration mismatch was blessed");
        var multi = Copy(doc); multi["contexts"]!["worker"]!["buildDirectory"] = "multi";
        multi["contexts"]!["worker"]!["configurePreset"] = "multi";
        multi["contexts"]!["worker"]!["configuration"] = "Release";
        (report, _) = Workflow(root, multi, name: "multi"); AllSucceeded(report);
        Check(File.Exists(Path.Combine(root, "multi", "Release", workerExecutableName)), "Multi-config executable missing");
        Pass("configure failure/recovery and multi-config target resolution");

        var regen = Copy(doc); regen["contexts"]!["worker"]!["buildDirectory"] = "regen";
        (report, _) = Workflow(root, regen, name: "regen-initial"); AllSucceeded(report);
        File.AppendAllText(cmakeList, "\n# equivalent regeneration\n");
        var regenExisting = Copy(regen); regenExisting["nodes"]!.AsArray().RemoveAt(0);
        regenExisting["nodes"]![0]!["dependsOn"] = new JsonArray();
        (report, _) = Workflow(root, regenExisting, expected: 1, name: "regen-strict");
        Check(Status(report, "build") == "failed" && Status(report, "run") == "blocked"
            && S(Item(report, "build")["error"]).Contains("Unverified"), "Strict regeneration was certified");
        regenExisting["contexts"]!["worker"]!["provenancePolicy"] = "allowUnverified";
        (report, _) = Workflow(root, regenExisting, name: "regen-allow"); AllSucceeded(report);
        Check(S(Item(report, "build")["observationAfter"]?["presetState"]) == "GenerationChanged", "Generation change was hidden");

        var drift = Copy(doc); drift["contexts"]!["worker"]!["buildDirectory"] = "drift";
        drift["nodes"] = JsonNode.Parse("""
        [
          {"id":"configure","kind":"configure","context":"worker"},
          {"id":"build","kind":"build","context":"worker","target":"worker","dependsOn":["configure"]},
          {"id":"clobber","kind":"invoke","context":"worker","target":"clobber_worker","dependsOn":["build"]},
          {"id":"run","kind":"run","from":"build","dependsOn":["clobber"]}
        ]
        """);
        (report, output) = Workflow(root, drift, expected: 1, name: "artifact-drift");
        Check(Status(report, "run") == "failed" && S(Item(report, "run")["error"]).Contains("changed since")
            && !File.Exists(Path.Combine(output, "run.stdout.log")), "Changed executable was run");
        Pass("regeneration policies and artifact identity guard");

        var nested = Copy(explicitDoc);
        nested["contexts"]!["worker"]!["mode"] = "adopt";
        nested["contexts"]!["worker"]!["provenancePolicy"] = "allowUnverified";
        nested["nodes"]![0]!["target"] = "nested";
        (report, output) = Workflow(root, nested, expected: 1, name: "nested");
        Check(Status(report, "campaign") == "failed" && (Log(report, output, "campaign") + Log(report, output, "campaign", "stderr")).Contains("Unsafe nested build"), "Nested build deadlocked or ran");
        nested["nodes"]![0]!["target"] = "prepared_nested";
        nested["nodes"]![0]!["timeoutSeconds"] = 10;
        (report, output) = Workflow(root, nested, expected: 1, name: "prepared-nested");
        Check((Log(report, output, "campaign") + Log(report, output, "campaign", "stderr")).Contains("Unsafe nested build")
            && !S(Item(report, "campaign")["error"]).Contains("Timed out"), "Prepared nested CLI deadlocked");
        Pass("nested CLI ownership guard");
    }
}
