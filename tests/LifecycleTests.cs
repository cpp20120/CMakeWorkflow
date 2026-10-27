using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class LifecycleTests
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);

    private static void Rejected(string root, JsonObject doc, string fragment)
    {
        var manifest = Write(Path.Combine(root, "rejected-" + Guid.NewGuid().ToString("N") + ".json"), doc.ToJsonString());
        var before = Fingerprint(root);
        Check(Command(Cli, ["workflow", "plan", manifest], 1).Output.Contains(fragment), "Unexpected plan rejection");
        var results = Path.Combine(root, "rejected-results-" + Guid.NewGuid().ToString("N"));
        Check(Command(Cli, ["workflow", "run", manifest, "--results-dir", results], 1).Output.Contains("Preflight rejected")
            && !Directory.Exists(results) && Fingerprint(root) == before, "Rejected workflow mutated a tree");
    }

    private static JsonObject CancelAfter(string root, JsonObject doc, string marker, string name)
    {
        var manifest = Write(Path.Combine(root, name + ".json"), doc.ToJsonString());
        var results = Path.Combine(root, name + "-results");
        var info = new ProcessStartInfo(Cli) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (var arg in new[] { "workflow", "run", manifest, "--results-dir", results }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new Exception("Could not start cancellation test");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(marker))
            {
                Check(!process.HasExited && DateTime.UtcNow < deadline, $"Marker never appeared: {marker}");
                Thread.Sleep(30);
            }
            Check(kill(process.Id, 2) == 0 && process.WaitForExit(15000) && process.ExitCode == 130,
                "SIGINT did not cancel the CLI");
            var journal = Directory.EnumerateFiles(results, "run.json", SearchOption.AllDirectories).Single();
            return J(File.ReadAllText(journal));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            _ = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        }
    }

    internal static void Run()
    {
        using var temp = new TempTree();
        var root = temp.Path;
        var source = Path.Combine(root, "source"); CopyFixture("lifecycle", source);
        var rules = Path.Combine(source, "rules.cmake");
        var originalRules = File.ReadAllText(rules);
        var build = Path.Combine(root, "tree");
        var doc = J("""
        {"schemaVersion":2,"contexts":{"w":{"buildDirectory":"tree","sourceDirectory":"source","configuration":"Debug","configurePreset":"debug","mode":"managed"}},"nodes":[
          {"id":"configure","kind":"configure","context":"w"},
          {"id":"build","kind":"build","context":"w","target":"worker","dependsOn":["configure"]},
          {"id":"run","kind":"run","from":"build"}
        ]}
        """);
        var observe = J("""{"schemaVersion":1,"contexts":{"w":{"buildDirectory":"absent","mode":"observe"}},"nodes":[{"id":"inspect","kind":"inspect","context":"w"}]}""");
        var observeManifest = Write(Path.Combine(root, "observe-plan.json"), observe.ToJsonString());
        var before = Fingerprint(root);
        Check(Command(Cli, ["workflow", "plan", observeManifest]).Output.Contains("mode=observe") && S(Inspect(Path.Combine(root, "absent"))["state"]) == "Unknown"
            && Fingerprint(root) == before && !Directory.Exists(Path.Combine(root, "absent")), "Observe mutated absent tree");
        var (report, _) = Workflow(root, observe, name: "observe"); AllSucceeded(report);
        Check(!Directory.Exists(Path.Combine(root, "absent")), "Inspect created a tree");
        var guarded = Write(Path.Combine(root, "guarded.json"), observe.ToJsonString());
        Command(Cli, ["workflow", "run", guarded, "--results-dir", Path.Combine(root, "guard-results")],
            environment: new() { ["CMAKE_WORKFLOW_OWNED_TREES"] = "[\"" + Path.Combine(root, "absent").Replace("\\", "\\\\") + "\"]" });
        Pass("Observe is read-only, including under inherited ownership");

        Configure(source, build);
        var adopted = Copy(doc); adopted["contexts"]!["w"]!["mode"] = "adopt";
        adopted["nodes"]!.AsArray().RemoveAt(0);
        adopted["nodes"]![0]!["dependsOn"] = new JsonArray();
        var configured = File.ReadAllText(Path.Combine(build, "configured"));
        var queries = Fingerprint(Path.Combine(build, ".cmake", "api", "v1", "query"));
        foreach (var schema in new[] { 1, 2 })
        {
            adopted["schemaVersion"] = schema;
            (report, _) = Workflow(root, adopted, name: "adopt-" + schema); AllSucceeded(report);
            Check(S(Item(report, "build")["sessionBefore"]?["provenance"]) == "AdoptedUnverified"
                && S(Item(report, "run")["observedContext"]?["presetState"]) == "Unknown", "Adoption certified preset identity");
        }
        Check(File.ReadAllText(Path.Combine(build, "configured")) == configured
            && Fingerprint(Path.Combine(build, ".cmake", "api", "v1", "query")) == queries
            && !File.Exists(Path.Combine(build, "cmake-workflow", "configure.json")), "Adoption configured tree");
        var implicitDoc = Copy(adopted); implicitDoc["contexts"]!["w"]!.AsObject().Remove("mode");
        implicitDoc["contexts"]!["w"]!["provenancePolicy"] = "allowUnverified";
        Rejected(root, implicitDoc, "Explicitly set mode");
        foreach (var (field, value, fragment) in new[] { ("sourceDirectory", ".", "source"),
                     ("configuration", "Release", "disagrees"), ("generator", "Ninja Multi-Config", "generator") })
        {
            var wrong = Copy(adopted); wrong["contexts"]!["w"]![field] = value;
            Rejected(root, wrong, fragment);
        }
        Pass("explicit IDE-tree adoption, schemas 1/2 and observable conflicts");

        var repeated = Copy(doc);
        repeated["nodes"]!.AsArray().Add(J("""{"id":"build2","kind":"build","context":"w","target":"worker","dependsOn":["run"]}"""));
        repeated["nodes"]!.AsArray().Add(J("""{"id":"run2","kind":"run","from":"build2"}"""));
        (report, var output) = Workflow(root, repeated, name: "managed-repeated"); AllSucceeded(report);
        var configuredAfter = File.ReadAllText(Path.Combine(build, "configured"));
        Check(configuredAfter == configured + "configure\n",
            $"Managed context configured too often: before={configured.Length}, after={configuredAfter.Length}, " +
            $"expected={configured.Length + "configure\n".Length} characters");
        Check(File.ReadAllText(Path.Combine(build, "environment")) == "inherited",
            "Managed configure did not inherit preset environment");
        Check(S(Item(report, "configure")["sessionAfter"]?["generationChange"]) == "ExplicitConfigure",
            "Managed configure session did not record ExplicitConfigure");
        // Compare against the CLI's resolved context without introducing a
        // project reference to CMakeWorkflow.CMake into the black-box test.
        // On macOS, /var may resolve to the physically equivalent /private/var.
        var requested = Item(report, "configure")["requestedContext"]!;
        var resolvedSource = S(requested["sourceDirectory"]);
        var resolvedBuild = S(requested["buildDirectory"]);
        Check(File.Exists(Path.Combine(resolvedSource, "CMakePresets.json"))
              && File.ReadAllText(Path.Combine(resolvedSource, "CMakeLists.txt"))
                 == File.ReadAllText(Path.Combine(source, "CMakeLists.txt"))
              && File.ReadAllText(Path.Combine(resolvedBuild, "configured")) == configuredAfter,
            "Managed configure resolved unexpected source/build directories");
        var expectedArguments = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            "--preset", "debug", "-S", resolvedSource,
            "-B", resolvedBuild
        });
        var actualArguments = Item(report, "configure")["configure"]?["arguments"]?.ToJsonString();
        Check(actualArguments == expectedArguments,
            $"Managed configure arguments differ: expected={expectedArguments}, actual={actualArguments}");
        Check(Log(report, output, "build2").Contains("no work to do"),
            "Repeated native build did not remain a no-op");
        Pass("managed configure invocation and repeated native build requests");

        foreach (var policy in new[] { "strict", "allowUnverified" })
        {
            File.WriteAllText(rules, originalRules);
            var regen = Copy(repeated);
            regen["contexts"]!["w"]!["provenancePolicy"] = policy;
            regen["nodes"]![2]!["arguments"] = new JsonArray("write", rules, originalRules + "# equivalent regeneration\n");
            (report, output) = Workflow(root, regen, expected: policy == "strict" ? 1 : 0, name: "regen-" + policy);
            var state = Item(report, "build2")["sessionAfter"]!;
            Check(S(state["generationChange"]) == "ObservedDuringBuildRequest"
                && state["observedPropertiesChanged"]!.GetValue<bool>() == false
                && S(state["provenance"]) == "Unverified"
                && S(Item(report, "build2")["buildVerification"]?["verification"]) == "BackendSucceeded", "Regeneration facts conflated");
            if (policy == "strict") Check(Status(report, "run2") == "blocked" && !File.Exists(Path.Combine(output, "run2.stdout.log")), "Strict ran after uncertain generation");
            else Check(Log(report, output, "run2").Contains("EXECUTED"), "AllowUnverified blocked equivalent generation");
        }
        Pass("Ninja regeneration, strict/allow policies and build verification separation");

        File.WriteAllText(rules, originalRules);
        var invoked = Copy(repeated);
        invoked["contexts"]!["w"]!["provenancePolicy"] = "allowUnverified";
        invoked["nodes"]![2]!["arguments"] = new JsonArray("write", rules, originalRules + "# regenerate on invoke\n");
        invoked["nodes"]!.AsArray().Insert(3, J("""{"id":"invoke","kind":"invoke","context":"w","target":"help","dependsOn":["run"]}"""));
        invoked["nodes"]![4]!["dependsOn"] = new JsonArray("invoke");
        (report, _) = Workflow(root, invoked, name: "invoke-regeneration"); AllSucceeded(report);
        Check(S(Item(report, "invoke")["sessionAfter"]?["generationChange"]) == "ObservedDuringTargetInvocation"
            && Item(report, "invoke")["buildVerification"] is null, "Invoke was reported as native Build verification");
        File.WriteAllText(rules, originalRules);
        invoked["contexts"]!["w"]!["provenancePolicy"] = "strict";
        (report, var invokeOutput) = Workflow(root, invoked, expected: 1, name: "invoke-strict");
        Check(Status(report, "invoke") == "failed" && S(Item(report, "invoke")["experimentResult"]) == "succeeded"
            && S(Item(report, "invoke")["exitCode"]) == "0" && Status(report, "run2") == "blocked"
            && !File.Exists(Path.Combine(invokeOutput, "run2.stdout.log")), "Strict invoke regeneration was treated as native failure or trusted");
        Pass("Invoke regeneration remains separate from native build verification");

        foreach (var (content, fragment) in new[] {
                     ("add_library(worker STATIC slow.cpp)\n", "target type"),
                     ("add_custom_target(worker)\n", "UTILITY"),
                     (originalRules + "set(CMAKE_BUILD_TYPE Release CACHE STRING \"\" FORCE)\n", "configuration") })
        {
            File.WriteAllText(rules, originalRules);
            var changed = Copy(repeated);
            changed["contexts"]!["w"]!["provenancePolicy"] = "allowUnverified";
            changed["nodes"]![2]!["arguments"] = new JsonArray("write", rules, content);
            (report, var conflictOutput) = Workflow(root, changed, expected: 1, name: "conflict-" + fragment.Replace(' ', '-'));
            Check(Status(report, "build2") == "failed" && Status(report, "run2") == "blocked"
                && S(Item(report, "build2")["error"]).Contains(fragment)
                && !File.Exists(Path.Combine(conflictOutput, "run2.stdout.log")), "Observed target/configuration conflict was ignored");
        }
        Pass("real regenerated target type and configuration conflicts block execution");

        foreach (var policy in new[] { "strict", "allowUnverified" })
        {
            File.WriteAllText(rules, originalRules);
            var external = Copy(repeated);
            external["contexts"]!["w"]!["provenancePolicy"] = policy;
            external["nodes"]![2]!["arguments"] = new JsonArray(
                "external", "cmake", "--preset", "debug", "-S", source, "-B", build);
            (report, output) = Workflow(root, external, expected: policy == "strict" ? 1 : 0, name: "external-" + policy);
            Check(S(Item(report, "build2")["sessionBefore"]?["generationChange"]) == "ExternalOrUnknown", "External configure was attributed to own build");
            if (policy == "strict") Check(!File.Exists(Path.Combine(output, "build2.stdout.log")), "Strict continued after external configure");
            else Check(S(Item(report, "run2")["sessionAfter"]?["provenance"]) == "Unverified", "External configure was certified");
        }
        Pass("external configure uncertainty follows explicit policy");

        File.WriteAllText(rules, originalRules);
        var stale = Copy(doc);
        stale["contexts"]!["w"]!["provenancePolicy"] = "allowUnverified";
        stale["nodes"]!.AsArray().Add(J("""{"id":"configure2","kind":"configure","context":"w","dependsOn":["run"]}"""));
        stale["nodes"]!.AsArray().Add(J("""{"id":"stale-run","kind":"run","from":"build","dependsOn":["configure2"]}"""));
        (report, output) = Workflow(root, stale, expected: 1, name: "stale-verification");
        Check(S(Item(report, "stale-run")["error"]).Contains("Generation changed since")
            && !File.Exists(Path.Combine(output, "stale-run.stdout.log")), "Old build verification reused after configure");
        Pass("new configure invalidates previous executable verification");

        File.WriteAllText(rules, "message(FATAL_ERROR \"FAILURE\")\n");
        (report, _) = Workflow(root, doc, expected: 1, name: "configure-failure");
        Check(S(J(File.ReadAllText(Path.Combine(build, "cmake-workflow", "configure.json")))["state"]) == "Failed"
            && Status(report, "run") == "blocked", "Old configure success survived failure");
        Rejected(root, adopted, "ConfigureFailed");
        File.WriteAllText(rules, originalRules);
        (report, _) = Workflow(root, doc, name: "recovery"); AllSucceeded(report);
        var wrongConfig = Copy(doc); wrongConfig["contexts"]!["w"]!["configuration"] = "Release";
        (report, _) = Workflow(root, wrongConfig, expected: 1, name: "rejected-configure");
        Check(S(J(File.ReadAllText(Path.Combine(build, "cmake-workflow", "configure.json")))["state"]) == "Rejected", "Incompatible successful configure was blessed");
        Workflow(root, doc, name: "recover-rejected");
        Pass("configure failure, incompatible observation and explicit recovery");

        var index = S(Inspect(build)["indexFile"]);
        var originalIndex = File.ReadAllBytes(index);
        foreach (var content in new[] { "{", "{}" })
        {
            File.WriteAllText(index, content);
            var snapshot = Fingerprint(build);
            Check(S(Inspect(build)["state"]) == "Unknown", "Corrupt File API accepted");
            Rejected(root, adopted, "Unknown");
            Check(Fingerprint(build) == snapshot, "Corrupt observation mutated build tree");
        }
        File.WriteAllBytes(index, originalIndex);
        var indexJson = J(System.Text.Encoding.UTF8.GetString(originalIndex));
        var modelName = indexJson["objects"]!.AsArray().Single(x => S(x?["kind"]) == "codemodel")!["jsonFile"]!.ToString();
        var model = Path.Combine(Path.GetDirectoryName(index)!, modelName);
        var modelBytes = File.ReadAllBytes(model);
        File.Delete(model);
        Rejected(root, adopted, "Unknown");
        File.WriteAllBytes(model, modelBytes);
        Pass("missing and corrupt File API snapshots fail closed");

        if (!OperatingSystem.IsWindows())
        {
            var slow = Copy(adopted);
            slow["nodes"] = JsonNode.Parse("""[{"id":"build","kind":"build","context":"w","target":"slow"},{"id":"run","kind":"run","from":"build"}]""");
            var canceled = CancelAfter(root, slow, Path.Combine(build, "building"), "cancel-build");
            Check(S(canceled["status"]) == "canceled" && Status(canceled, "run") == "canceled"
                && !File.Exists(Path.Combine(build, "BUILD_COMPLETED")), "Canceled native build completed or ran dependent node");
            Workflow(root, adopted, name: "after-cancel");
            Pass("SIGINT during native build stops process tree and blocks dependents");

            File.WriteAllText(rules, originalRules + "\nfile(WRITE \"${CMAKE_BINARY_DIR}/configuring\" \"started\")\n"
                + "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 30)\n"
                + "file(WRITE \"${CMAKE_BINARY_DIR}/CONFIGURE_COMPLETED\" \"done\")\n");
            var configureCanceled = CancelAfter(root, doc, Path.Combine(build, "configuring"), "cancel-configure");
            Check(Status(configureCanceled, "configure") == "canceled"
                && S(J(File.ReadAllText(Path.Combine(build, "cmake-workflow", "configure.json")))["state"]) == "Interrupted"
                && !File.Exists(Path.Combine(build, "CONFIGURE_COMPLETED")), "Canceled configure retained success or completed");
            File.WriteAllText(rules, originalRules);
            (report, _) = Workflow(root, doc, name: "recover-after-configure-cancel"); AllSucceeded(report);
            Pass("SIGINT during configure invalidates provenance and permits explicit recovery");
        }
    }
}
