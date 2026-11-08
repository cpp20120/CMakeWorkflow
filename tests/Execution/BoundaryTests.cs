using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Execution;

internal static class BoundaryTests
{
    internal static void Run()
    {
        using TempTree temp = new();
        string root = temp.Path;
        string source = Path.Combine(root, "source");
        CopyFixture("boundary", source);
        string cmakeList = Path.Combine(source, "CMakeLists.txt");
        File.WriteAllText(cmakeList,
            "set(TEST_CLI \"" + Cli.Replace('\\', '/') + "\")\n" + File.ReadAllText(cmakeList));
        File.AppendAllText(cmakeList, """
                                      add_custom_target(harness_probe
                                        COMMAND "${CMAKE_COMMAND}" "-DOUTPUT=${CMAKE_BINARY_DIR}/harness-results"
                                                -P "${CMAKE_CURRENT_SOURCE_DIR}/fake_harness.cmake" VERBATIM)
                                      """);
        string build = Path.Combine(root, "managed");
        string workerExecutableName = OperatingSystem.IsWindows() ? "worker.exe" : "worker";
        JsonObject doc = J(/*lang=json,strict*/ """
                    {"schemaVersion":2,"contexts":{"worker":{"buildDirectory":"managed","sourceDirectory":"source","configurePreset":"debug","configuration":"Debug"}},"nodes":[
                      {"id":"configure","kind":"configure","context":"worker"},
                      {"id":"build","kind":"build","context":"worker","target":"worker","dependsOn":["configure"]},
                      {"id":"run","kind":"run","from":"build"},
                      {"id":"test","kind":"ctest","context":"worker","dependsOn":["run"]}
                    ]}
                    """);
        string planFile = Write(Path.Combine(root, "initial-plan.json"), doc.ToJsonString());
        string before = Fingerprint(root);
        string plan = Command(Cli, ["workflow", "plan", planFile]).Output;
        Check(plan.Contains("Unknown", StringComparison.Ordinal) && plan.Contains("Runtime", StringComparison.Ordinal) && plan.Contains("Unverified", StringComparison.Ordinal)
              && Fingerprint(root) == before && !Directory.Exists(build), "Plan mutated an absent context");
        Check(S(Inspect(build)["state"]) == "Unknown", "Absent observation was guessed");
        (JsonObject? report, string? output) = Workflow(root, doc, name: "managed");
        AllSucceeded(report);
        Check(S(Item(report, "configure")["configure"]?["state"]) == "Succeeded"
              && S(Item(report, "build")["buildVerification"]?["verification"]) == "BackendSucceeded"
              && S(Item(report, "run")["artifact"]?["materialization"]) == "VerifiedAgainstBuild"
              && S(Item(report, "run")["experimentResult"]) == "succeeded", "Build/experiment facts were conflated");
        JsonObject observed = Inspect(build);
        Check(S(observed["freshness"]) == "Unverified" && S(observed["presetState"]) == "RecordedInvocation"
                                                       && observed["hasCacheReply"]!.GetValue<bool>() &&
                                                       observed["toolchains"]!.AsArray().Count > 0
                                                       && File.ReadAllText(Path.Combine(build, "preset-env.txt")) ==
                                                       "from-cmake-preset"
                                                       && Log(report, output, "run").Contains("WORKER_RAN", StringComparison.Ordinal),
            "Managed preset/environment observation failed");
        Pass("read-only plan and explicit managed configure through inherited CMake preset");

        JsonObject existing = Copy(doc);
        existing["nodes"]!.AsArray().RemoveAt(0);
        existing["nodes"]![0]!["dependsOn"] = new JsonArray();
        JsonObject mismatch = Copy(existing);
        mismatch["contexts"]!["worker"]!["configurePreset"] = "same-cache";
        Check(Plan(root, mismatch, 1).Contains("conflicts", StringComparison.Ordinal), "Preset identity inferred from matching cache");
        mismatch = Copy(existing);
        mismatch["contexts"]!["worker"]!["configuration"] = "Release";
        Check(Plan(root, mismatch, 1).Contains("disagrees", StringComparison.Ordinal), "Configuration mismatch accepted");
        _ = Command("cmake", ["--preset", "same-cache", "-S", source, "-B", build]);
        Check(Plan(root, existing, 1).Contains("Unverified", StringComparison.Ordinal), "External configure retained recorded provenance");
        JsonObject adopted = Copy(existing);
        adopted["contexts"]!["worker"]!["mode"] = "adopt";
        adopted["contexts"]!["worker"]!["provenancePolicy"] = "allowUnverified";
        (report, _) = Workflow(root, adopted, name: "adopt");
        AllSucceeded(report);
        Check(S(Item(report, "build")["observedContext"]?["presetState"]) == "GenerationChanged",
            "Adoption certified preset identity");
        (report, _) = Workflow(root, doc, name: "restore");
        AllSucceeded(report);
        Pass("preset identity, known conflicts and explicit adoption");

        JsonObject campaign = Copy(existing);
        campaign["nodes"] =
            JsonNode.Parse(
                """[{"id":"prepare","kind":"build","context":"worker","target":"fuzz_campaign"},{"id":"run","kind":"run","from":"prepare"}]""");
        Check(Plan(root, campaign, 1).Contains("invoke", StringComparison.Ordinal), "UTILITY target accepted as build");
        string unsafeManifest = Write(Path.Combine(root, "unsafe.json"), campaign.ToJsonString());
        string unsafeResults = Path.Combine(root, "unsafe-results");
        Check(Command(Cli, ["workflow", "run", unsafeManifest, "--results-dir", unsafeResults], 1).Output
                  .Contains("Preflight rejected", StringComparison.Ordinal)
              && !Directory.Exists(unsafeResults) && !File.Exists(Path.Combine(build, "CAMPAIGN_RAN")),
            "Unsafe campaign ran implicitly");
        JsonObject dangerous = Copy(campaign);
        dangerous["nodes"]!.AsArray()
            .Add(J(/*lang=json,strict*/ """{"id":"independent-campaign","kind":"invoke","context":"worker","target":"fuzz_campaign"}"""));
        string dangerousManifest = Write(Path.Combine(root, "dangerous.json"), dangerous.ToJsonString());
        Check(Command(Cli,
                    ["workflow", "run", dangerousManifest, "--results-dir", Path.Combine(root, "dangerous-results")], 1)
                .Output.Contains("Preflight rejected", StringComparison.Ordinal) && !File.Exists(Path.Combine(build, "CAMPAIGN_RAN")),
            "Independent campaign ran despite preflight error");
        foreach (string? verb in new[] { "build", "run" })
        {
            Check(RunCli(1, "target", verb, "--build-dir", build, "--target", "fuzz_campaign").Contains("UTILITY", StringComparison.Ordinal),
                "Target CLI ran a campaign");
        }

        JsonObject explicitDoc = Copy(campaign);
        explicitDoc["nodes"] =
            JsonNode.Parse("""[{"id":"campaign","kind":"invoke","context":"worker","target":"fuzz_campaign"}]""");
        (report, _) = Workflow(root, explicitDoc, name: "explicit-campaign");
        Check(Status(report, "campaign") == "succeeded" && File.Exists(Path.Combine(build, "CAMPAIGN_RAN"))
                                                        && Item(report, "campaign")["buildVerification"] is null,
            "Invoke was mislabeled or did not execute");
        File.Delete(Path.Combine(build, "CAMPAIGN_RAN"));
        _ = RunCli("target", "invoke", "--build-dir", build, "--target", "fuzz_campaign");
        Check(File.Exists(Path.Combine(build, "CAMPAIGN_RAN")), "Explicit target invoke did not run");
        _ = RunCli("target", "build", "--build-dir", build, "--target", "native_library");
        Pass("native Build versus intentional Invoke and UTILITY safety");

        string limited = Path.Combine(root, "limited");
        _ = Command("cmake", ["--preset", "debug", "-S", source, "-B", limited]);
        JsonObject limitedDoc = Copy(adopted);
        limitedDoc["contexts"]!["worker"] =
            J(/*lang=json,strict*/ """{"buildDirectory":"limited","configuration":"Debug","mode":"adopt"}""");
        Check(
            Plan(root, limitedDoc, 1).Contains("Unknown", StringComparison.Ordinal) && !File.Exists(Path.Combine(limited, workerExecutableName)),
            "Missing File API metadata caused build");
        Check(S(Inspect(limited)["state"]) == "ObservedLimited", "Limited observation misclassified");
        limitedDoc["contexts"]!["worker"]!["mode"] = "managed";
        limitedDoc["contexts"]!["worker"]!["sourceDirectory"] = "source";
        limitedDoc["contexts"]!["worker"]!["configurePreset"] = "debug";
        limitedDoc["nodes"]!.AsArray().Insert(0, doc["nodes"]![0]!.DeepClone());
        limitedDoc["nodes"]![1]!["dependsOn"] = new JsonArray("configure");
        (report, _) = Workflow(root, limitedDoc, name: "repair-file-api");
        AllSucceeded(report);
        Pass("unknown native target metadata requires an explicit configure");

        string bridge = Path.Combine(root, "bridge");
        _ = Command("cmake", [
            "-S", Path.Combine(Root, "tests", "fixture"), "-B", bridge, "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Debug", "-DWORKFLOW_RUNNER_EXECUTABLE=" + Cli.Replace('\\', '/')
        ]);
        _ = Command("cmake", ["--build", bridge, "--target", "fixture_bridge_worker"]);
        Check(Inspect(bridge)["targets"]!.AsArray().Any(t => S(t?["evidence"]) == "BridgeManifest"),
            "Bridge fallback was lost");
        string stale = Path.Combine(build, "cmake-workflow", "targets", "Debug", "fuzz_campaign.json");
        _ = Write(stale, "{\"schemaVersion\":1,\"target\":\"fuzz_campaign\",\"configuration\":\"Debug\",\"executable\":\""
                     + Path.Combine(build, workerExecutableName).Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}");
        Check(RunCli(1, "target", "run", "--build-dir", build, "--target", "fuzz_campaign").Contains("UTILITY", StringComparison.Ordinal),
            "Stale bridge manifest overrode current codemodel");
        Pass("bridge fallback and current codemodel priority");

        JsonObject failed = Copy(doc);
        failed["contexts"]!["worker"]!["configurePreset"] = "failure";
        (report, _) = Workflow(root, failed, expected: 1, name: "failed-configure");
        Check(Status(report, "configure") == "failed" && Status(report, "build") == "blocked"
                                                      && S(Inspect(build)["state"]) == "ConfigureFailed",
            "Failed configure reused stale success");
        (report, _) = Workflow(root, doc, name: "recover");
        AllSucceeded(report);
        JsonObject wrong = Copy(doc);
        wrong["contexts"]!["worker"]!["buildDirectory"] = "wrong-config";
        wrong["contexts"]!["worker"]!["configuration"] = "Release";
        (report, _) = Workflow(root, wrong, expected: 1, name: "wrong-config");
        Check(Status(report, "configure") == "failed" && Status(report, "build") == "blocked",
            "Requested configuration mismatch was blessed");
        JsonObject multi = Copy(doc);
        multi["contexts"]!["worker"]!["buildDirectory"] = "multi";
        multi["contexts"]!["worker"]!["configurePreset"] = "multi";
        multi["contexts"]!["worker"]!["configuration"] = "Release";
        (report, _) = Workflow(root, multi, name: "multi");
        AllSucceeded(report);
        Check(File.Exists(Path.Combine(root, "multi", "Release", workerExecutableName)),
            "Multi-config executable missing");
        Pass("configure failure/recovery and multi-config target resolution");

        JsonObject regen = Copy(doc);
        regen["contexts"]!["worker"]!["buildDirectory"] = "regen";
        (report, _) = Workflow(root, regen, name: "regen-initial");
        AllSucceeded(report);
        File.AppendAllText(cmakeList, "\n# equivalent regeneration\n");
        JsonObject regenExisting = Copy(regen);
        regenExisting["nodes"]!.AsArray().RemoveAt(0);
        regenExisting["nodes"]![0]!["dependsOn"] = new JsonArray();
        (report, _) = Workflow(root, regenExisting, expected: 1, name: "regen-strict");
        Check(Status(report, "build") == "failed" && Status(report, "run") == "blocked"
                                                  && S(Item(report, "build")["error"]).Contains("Unverified", StringComparison.Ordinal),
            "Strict regeneration was certified");
        regenExisting["contexts"]!["worker"]!["provenancePolicy"] = "allowUnverified";
        (report, _) = Workflow(root, regenExisting, name: "regen-allow");
        AllSucceeded(report);
        Check(S(Item(report, "build")["observationAfter"]?["presetState"]) == "GenerationChanged",
            "Generation change was hidden");

        JsonObject drift = Copy(doc);
        drift["contexts"]!["worker"]!["buildDirectory"] = "drift";
        drift["nodes"] = JsonNode.Parse("""
                                        [
                                          {"id":"configure","kind":"configure","context":"worker"},
                                          {"id":"build","kind":"build","context":"worker","target":"worker","dependsOn":["configure"]},
                                          {"id":"clobber","kind":"invoke","context":"worker","target":"clobber_worker","dependsOn":["build"]},
                                          {"id":"run","kind":"run","from":"build","dependsOn":["clobber"]}
                                        ]
                                        """);
        (report, output) = Workflow(root, drift, expected: 1, name: "artifact-drift");
        Check(Status(report, "run") == "failed" && S(Item(report, "run")["error"]).Contains("changed since", StringComparison.Ordinal)
                                                && !File.Exists(Path.Combine(output, "run.stdout.log")),
            "Changed executable was run");
        Pass("regeneration policies and artifact identity guard");

        JsonObject nested = Copy(explicitDoc);
        nested["contexts"]!["worker"]!["mode"] = "adopt";
        nested["contexts"]!["worker"]!["provenancePolicy"] = "allowUnverified";
        nested["nodes"]![0]!["target"] = "nested";
        (report, output) = Workflow(root, nested, expected: 1, name: "nested");
        Check(
            Status(report, "campaign") == "failed" &&
            (Log(report, output, "campaign") + Log(report, output, "campaign", "stderr")).Contains(
                "Unsafe nested build", StringComparison.Ordinal), "Nested build deadlocked or ran");
        nested["nodes"]![0]!["target"] = "prepared_nested";
        nested["nodes"]![0]!["timeoutSeconds"] = 10;
        (report, output) = Workflow(root, nested, expected: 1, name: "prepared-nested");
        string preparedOutput = Log(report, output, "campaign") + Log(report, output, "campaign", "stderr");
        string preparedError = S(Item(report, "campaign")["error"]);
        Check(preparedOutput.Contains("Unsafe nested build", StringComparison.Ordinal)
              && !preparedError.Contains("Timed out", StringComparison.Ordinal),
            $"Prepared nested CLI ownership guard failed: status={Status(report, "campaign")}, "
            + $"error={preparedError}, output={preparedOutput}");
        Pass("nested CLI ownership guard");
        // Exercise the real CLI boundary: CMake emits two harness result markers,
        // CMakeWorkflow snapshots their files and History imports raw samples.
        string harnessBuild = Path.Combine(root, "harness-native");
        Configure(source, harnessBuild);
        JsonObject harnessDoc = J(/*lang=json,strict*/ """
                           {"schemaVersion":2,"contexts":{"w":{"buildDirectory":"harness-native","mode":"adopt","provenancePolicy":"allowUnverified"}},
                             "nodes":[{"id":"probe","kind":"invoke","context":"w","target":"harness_probe"}]}
                           """);
        (report, output) = Workflow(root, harnessDoc, name: "auto-harness");
        AllSucceeded(report);
        JsonObject harnessOutputs = Item(report, "probe")["outputs"]!.AsObject();
        Check(
            harnessOutputs.ContainsKey("dagflow-0-summary.json") &&
            harnessOutputs.ContainsKey("dagflow-1-summary.json"),
            "Harness markers did not produce two immutable snapshots");
        string firstSaved = Path.Combine(output, S(harnessOutputs["dagflow-0-runs.jsonl"]));
        Check(File.Exists(firstSaved), "Harness snapshot missing");
        string catalog = Path.Combine(root, "auto-harness-history.sqlite");
        _ = Command(Cli, ["history", "import", output, "--db", catalog, "--json"]);
        JsonArray indexed = JsonNode.Parse(RunCli("history", "runs", "--db", catalog, "--json"))!.AsArray();
        Check(indexed.Count == 1, "Harness history import did not create one workflow run");
        JsonObject detail = J(RunCli("history", "show", S(indexed[0]!["id"]), "--db", catalog, "--json"));
        Check(detail["measurements"]!.AsArray().Count == 20
              && detail["artifacts"]!.AsArray().Count(a => S(a?["type"]) == "dagflow-runs") == 2,
            "Harness samples or snapshot artifacts missing from History");
        Pass("automatic multi-harness snapshot and History measurement ingestion");
    }
}