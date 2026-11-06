using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.History;
using CMakeWorkflow.History.Models;
using CMakeWorkflow.History.Storage;
using Microsoft.Data.Sqlite;

string root = Path.Combine(Path.GetTempPath(), "cmake-workflow-history-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string dbPath = Path.Combine(root, "history.sqlite");
    string resultRoot = Path.Combine(root, "results");
    string success = Path.Combine(resultRoot, "success");
    string failure = Path.Combine(resultRoot, "failure");
    string active = Path.Combine(resultRoot, "active");
    _ = Directory.CreateDirectory(success);
    _ = Directory.CreateDirectory(failure);
    _ = Directory.CreateDirectory(active);

    static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(name);
        }
    }

    static void Write(string dir, string file, string text) =>
        File.WriteAllText(Path.Combine(dir, file), text);

    static string Workflow(string status, double first, double second, string workers = "1") => $$"""
          {"schemaVersion":1,"workflow":"bench.json","manifestHash":"abc",
           "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
           "status":"{{status}}","nodes":[
             {"id":"build","kind":"build","status":"succeeded","dependencies":[],"buildDirectory":"/build"},
             {"id":"timing_main_1","kind":"run","status":"succeeded","dependencies":["build"],
              "arguments":["--scenario","idle","--workers","{{workers}}"],"processDurationSeconds":{{first}}},
             {"id":"timing_main_2","kind":"run","status":"succeeded","dependencies":["build"],
              "arguments":["--scenario","idle","--workers","{{workers}}"],"processDurationSeconds":{{second}}}]}
          """;

    Write(success, "run.json", Workflow("succeeded", 2, 4));
    Write(failure, "run.json", Workflow("failed", 4, 6));
    Write(active, "run.json", /*lang=json,strict*/ """{"schemaVersion":1,"workflow":"active","status":"running","nodes":[]}""");
    using (HistoryStore store = new(dbPath))
    {
        Check(store.Runs(50, null, null, null, null, null).Count == 0, "fresh DB");
        Check(store.Import(resultRoot, true).Contains("Imported 2", StringComparison.Ordinal), "completed and failed import");
        Check(store.Runs(50, "failed", null, null, null, null).Count == 1, "status filter");
        Check(store.Runs(50, null, "2026-01-02T00:00:00Z", null, null, null).Count == 0,
            "time filter");
        Check(store.Runs(50, null, null, null, "bench", "main").Count == 2,
            "workflow and variant filters");
        Check(store.Runs(50, null, null, null, null, null).Count == 2, "active skipped");
        Check(store.Import(resultRoot, true).Contains("unchanged 2", StringComparison.Ordinal), "idempotent import");
        Check(store.Import(Path.Combine(success, "run.json"), false).Contains("unchanged 1", StringComparison.Ordinal),
            "report file path");
        using StringWriter importOutput = new();
        TextWriter originalOutput = Console.Out;
        Console.SetOut(importOutput);
        try
        {
            Check(await HistoryCommand.ExecuteAsync([
                "import", success, "--db", dbPath,
                "--json"
            ], default) == 0, "JSON import exit");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        using (JsonDocument imported = JsonDocument.Parse(importOutput.ToString()))
        {
            Check(imported.RootElement.GetProperty("schemaVersion").GetInt32() == 1
                  && imported.RootElement.GetProperty("unchanged").GetInt32() == 1,
                "JSON import response");
        }

        HistoryRun row = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == success);
        List<HistoryMeasurement> measurements = store.Metrics(row.Id, "process_wall_time", "main", null);
        Check(measurements.Count == 2 && measurements.Select(x => x.Value).SequenceEqual([2, 4])
                                      && measurements.Select(x => x.SampleIndex).SequenceEqual([0, 1]), "raw samples");
        Check(store.Show(row.Id).Nodes.Count == 3, "nodes and dependencies");
        Write(success, "run.json", Workflow("succeeded", 3, 5));
        Check(store.Import(success, false).Contains("Imported 1", StringComparison.Ordinal), "changed report refresh");
        Check(store.Metrics(row.Id, null, null, null).Count == 2
              && store.Metrics(row.Id, null, null, null)[0].Value == 3, "no duplicate samples");
        Write(success, "run.json", /*lang=json,strict*/ """
                                   {"schemaVersion":1,"workflow":"bench.json","manifestHash":"abc",
                                    "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
                                    "status":"succeeded","nodes":[
                                      {"id":"timing_main_1","kind":"run","status":"succeeded",
                                       "arguments":["--scenario","idle","--workers","1"],
                                       "processDurationSeconds":7}]}
                                   """);
        Check(store.Import(success, false).Contains("Imported 1", StringComparison.Ordinal), "sample removal refresh");
        List<HistoryMeasurement> refreshed = store.Metrics(row.Id, null, null, null);
        Check(refreshed.Count == 1 && refreshed.Single().Value == 7,
            "removed source sample survived re-import");
    }

    using (HistoryStore reopened = new(dbPath))
    {
        Check(reopened.Runs(50, null, null, null, null, null).Count == 2, "reopen persistence");
        List<HistoryRun> rows = reopened.Runs(50, null, null, null, null, null);
        using StringWriter output = new();
        TextWriter old = Console.Out;
        Console.SetOut(output);
        try
        {
            Check(await HistoryCommand.ExecuteAsync([
                    "compare", rows.Single(x => x.Directory == success).Id,
                    rows.Single(x => x.Directory == failure).Id, "--db", dbPath, "--json"
                ], default) == 0,
                "comparison command");
        }
        finally
        {
            Console.SetOut(old);
        }

        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement comparison = json.RootElement[0];
        Check(comparison.GetProperty("status").GetString() == "unverified"
              && comparison.GetProperty("baseline").GetProperty("median").GetDouble() == 7
              && comparison.GetProperty("candidate").GetProperty("median").GetDouble() == 5
              && comparison.GetProperty("percentChange").ValueKind == JsonValueKind.Null,
            "unverified comparison must not emit a percentage");
        _ = output.GetStringBuilder().Clear();
        Console.SetOut(output);
        try
        {
            Check(await HistoryCommand.ExecuteAsync(["runs", "--db", dbPath, "--json"], default) == 0, "runs JSON");
        }
        finally
        {
            Console.SetOut(old);
        }

        Check(JsonDocument.Parse(output.ToString()).RootElement.GetArrayLength() == 2, "JSON output");
    }

    string bad = Path.Combine(root, "bad");
    _ = Directory.CreateDirectory(bad);
    Write(bad, "run.json", /*lang=json,strict*/ """{"schemaVersion":9,"workflow":"bad","status":"failed","nodes":[]}""");
    using (HistoryStore store = new(dbPath))
    {
        try
        {
            _ = store.Import(bad, false);
            throw new System.Diagnostics.UnreachableException("unsupported accepted");
        }
        catch (InvalidDataException e)
        {
            Check(e.Message.Contains("schemaVersion", StringComparison.Ordinal), "unsupported diagnostics");
        }

        Write(bad, "run.json", "{");
        try
        {
            _ = store.Import(bad, false);
            throw new System.Diagnostics.UnreachableException("malformed accepted");
        }
        catch (JsonException)
        {
        }

        string batch = Path.Combine(root, "batch");
        string good = Path.Combine(batch, "a-good");
        string corrupt = Path.Combine(batch, "z-corrupt");
        _ = Directory.CreateDirectory(good);
        _ = Directory.CreateDirectory(corrupt);
        Write(good, "run.json", Workflow("succeeded", 1, 1));
        Write(corrupt, "run.json", "{");
        int before = store.Runs(50, null, null, null, null, null).Count;
        try
        {
            _ = store.Import(batch, true);
            throw new System.Diagnostics.UnreachableException("partial batch accepted");
        }
        catch (JsonException)
        {
        }

        Check(store.Runs(50, null, null, null, null, null).Count == before, "atomic batch");
    }

    string captureOnly = Path.Combine(root, "capture-only");
    _ = Directory.CreateDirectory(captureOnly);
    Write(captureOnly, "cpu.results.json", /*lang=json,strict*/ """
                                           {"captureId":"cpu","backend":"etw","mode":"cpu","status":"complete",
                                            "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
                                            "rawTrace":"traces/raw/cpu.etl","rawTraceSha256":"abc",
                                            "eventSource":"ETW CPU sampling"}
                                           """);
    _ = Directory.CreateDirectory(Path.Combine(captureOnly, "traces", "normalized"));
    Write(Path.Combine(captureOnly, "traces", "normalized"), "cpu.etw.json", /*lang=json,strict*/ """
                                                                             {"schemaVersion":1,"backend":"etw","attribution":"target PID and time window",
                                                                              "targetCpuSamples":5,"readyLatencySamples":2,"readyLatencySumMilliseconds":2.5}
                                                                             """);
    using (HistoryStore store = new(dbPath))
    {
        Check(store.Import(captureOnly, false).Contains("Imported 1", StringComparison.Ordinal), "standalone capture");
        HistoryRun capture = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == captureOnly);
        Check(capture.Status == "succeeded"
              && store.Metrics(capture.Id, "targetCpuSamples", null, null).Single().Value == 5
              && store.Metrics(capture.Id, "readyLatencyMeanMilliseconds", null, null).Single().Value == 1.25
              && store.Show(capture.Id).Artifacts.Any(x => x.Type == "trace" && !x.Available),
            "ETW summary and missing trace link");
    }

    string commandRun = Path.Combine(root, "command-run");
    _ = Directory.CreateDirectory(commandRun);
    Write(commandRun, "run.json", /*lang=json,strict*/ """
                                  {"schemaVersion":1,"workflow":"hello.json",
                                   "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:00:01Z",
                                   "status":"succeeded","nodes":[
                                     {"id":"configure","kind":"configure","status":"succeeded","executable":"cmake"},
                                     {"id":"test","kind":"ctest","status":"succeeded","executable":"ctest"}]}
                                  """);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(commandRun, false);
        string id = store.Runs(50, null, null, null, null, null)
            .Single(x => x.Directory == commandRun).Id;
        Check(store.Show(id).Artifacts.All(x => x.Type != "executable"),
            "tool commands must not be indexed as file artifacts");
    }

    string fuzz = Path.Combine(root, "fuzz");
    _ = Directory.CreateDirectory(fuzz);
    Write(fuzz, "report.json", /*lang=json,strict*/ """
                               {"schemaVersion":1,"layer":"ranges","backend":"libfuzzer",
                                "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
                                "status":"failed","executable":"/build/fuzzer","executableSha256":"abc",
                                "crashArtifacts":["artifacts/crash-1"]}
                               """);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(fuzz, false);
        HistoryRun row = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == fuzz);
        Check(row.Format == "fuzz" && row.Status == "failed"
                                   && store.Show(row.Id).Measurements.Count == 0
                                   && store.Show(row.Id).Artifacts.Any(x => x.Type == "crash"),
            "fuzz report and missing metrics");
    }

    string campaign = Path.Combine(root, "campaign");
    _ = Directory.CreateDirectory(campaign);
    Write(campaign, "report.json", /*lang=json,strict*/ """{"checks":{"parking":{"status":"PASS"}}}""");
    using (HistoryStore store = new(dbPath))
    {
        Check(store.Import(campaign, false).Contains("skipped 1", StringComparison.Ordinal), "incomplete campaign skipped");
    }


#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
    string reportHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(campaign, "report.json"), CancellationToken.None))).ToLowerInvariant();
#pragma warning restore CA1308

    Write(campaign, "campaign-completion.json", $$"""
                                                  {"schemaVersion":1,"campaign":"weak-memory","report":"report.json",
                                                   "reportSha256":"{{reportHash}}","startedAt":"2026-01-01T00:00:00Z",
                                                   "finishedAt":"2026-01-01T00:01:00Z","status":"succeeded"}
                                                  """);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(campaign, false);
        HistoryRun row = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == campaign);
        Check(row.Format == "campaign" && row.Workflow == "weak-memory"
                                       && store.Show(row.Id).Artifacts.Count == 2, "completed generic campaign");
        Write(campaign, "report.json", /*lang=json,strict*/ """{"checks":{}}""");
        try
        {
            _ = store.Import(campaign, false);
            throw new System.Diagnostics.UnreachableException("campaign hash drift accepted");
        }
        catch (InvalidDataException)
        {
        }
    }

    string profile = Path.Combine(root, "profile");
    _ = Directory.CreateDirectory(profile);
    Write(profile, "run.json", Workflow("succeeded", 1, 2));
    Write(profile, "counter-comparison.json", /*lang=json,strict*/ """
                                              {"schemaVersion":1,"backend":"perf stat","variants":[
                                              {"name":"main","events":{"cycles":{"value":100,"unit":"count"}},
                                               "derived":{"ipc":{"value":2.5,"unit":"instructions/cycle"}}}]}
                                              """);
    string profile2 = Path.Combine(root, "profile2");
    _ = Directory.CreateDirectory(profile2);
    Write(profile2, "run.json", Workflow("succeeded", 2, 3));
    Write(profile2, "counter-comparison.json", /*lang=json,strict*/ """
                                               {"schemaVersion":1,"backend":"perf stat","variants":[
                                               {"name":"main","events":{"cycles":{"value":110,"unit":"ms"}}}]}
                                               """);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(profile, false);
        _ = store.Import(profile2, false);
        HistoryRun first = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == profile);
        HistoryRun second = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == profile2);
        Check(store.Metrics(first.Id, "cycles", null, null).Single().Value == 100, "counter import");
        Check(store.Metrics(first.Id, "ipc", null, null).Single().Value == 2.5
              && store.Metrics(first.Id, "ipc", null, null).Single().Method == "perf stat derived",
            "PMU derived counter import must remain distinct from raw events");
        using StringWriter output = new();
        TextWriter old = Console.Out;
        Console.SetOut(output);
        try
        {
            _ = await HistoryCommand.ExecuteAsync(["compare", first.Id, second.Id, "--db", dbPath, "--json"], default);
        }
        finally
        {
            Console.SetOut(old);
        }

        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Check(doc.RootElement.EnumerateArray().Any(x => x.GetProperty("metric").GetString() == "cycles"
                                                        && x.GetProperty("status").GetString() == "incompatible"),
            "unit compatibility");
        Write(profile, "counter-comparison.json", /*lang=json,strict*/ """
                                                  {"schemaVersion":1,"backend":"ETW","variants":[
                                                  {"name":"main","events":{"cycles":{"value":100,"unit":"ms"}}}]}
                                                  """);
        Check(store.Import(profile, false).Contains("Imported 1", StringComparison.Ordinal), "small metadata refresh");
        _ = output.GetStringBuilder().Clear();
        Console.SetOut(output);
        try
        {
            _ = await HistoryCommand.ExecuteAsync(["compare", first.Id, second.Id, "--db", dbPath, "--json"], default);
        }
        finally
        {
            Console.SetOut(old);
        }

        using JsonDocument methodDoc = JsonDocument.Parse(output.ToString());
        Check(methodDoc.RootElement.EnumerateArray().Any(x => x.GetProperty("metric").GetString() == "cycles"
                                                              && x.GetProperty("status").GetString() == "incompatible"),
            "method compatibility");
        Directory.Delete(profile, true);
        Check(!store.Show(first.Id).Run.SourceAvailable, "deleted source explicit");
    }

    string singleProfile = Path.Combine(root, "single-profile");
    _ = Directory.CreateDirectory(singleProfile);
    Write(singleProfile, "run.json", Workflow("succeeded", 1, 2));
    Write(singleProfile, "analysis.json", /*lang=json,strict*/ """
                                          [{"captureId":"capture_main","backend":"perf","mode":"counters",
                                            "counters":{"cycles:u":{"value":1234,"unit":""},
                                                        "instructions:u":{"value":5678,"unit":""}}}]
                                          """);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(singleProfile, false);
        string id = store.Runs(50, null, null, null, null, null)
            .Single(x => x.Directory == singleProfile).Id;
        Check(store.Metrics(id, "cycles:u", "main", null).Single().Value == 1234,
            "single-profile analysis counters");
    }

    string userProfile = Path.Combine(root, "profile-user-domain");
    string kernelProfile = Path.Combine(root, "profile-kernel-domain");
    foreach ((string? path, string? domain) in new[] { (userProfile, "user"), (kernelProfile, "kernel") })
    {
        _ = Directory.CreateDirectory(Path.Combine(path, "traces", "normalized"));
        Write(path, "run.json", Workflow("succeeded", 1, 2));
        Write(path, "counter-comparison.json",
            "{\"schemaVersion\":2,\"backend\":\"perf stat\",\"variants\":[" +
            "{\"name\":\"main\",\"events\":{},\"derived\":{" +
            "\"ipc\":{\"value\":1.25,\"unit\":\"instructions/cycle\",\"domain\":\"" + domain + "\"}}}]}");
    }

    Write(userProfile, "analysis.json", /*lang=json,strict*/ """
                                        [{"captureId":"capture_main","backend":"perf","mode":"cpu",
                                          "captureStatus":"complete","analysisStatus":"failed"}]
                                        """);
    Write(Path.Combine(userProfile, "traces", "normalized"), "capture_main.stacks.json", /*lang=json,strict*/ """
        {"schemaVersion":2,"analysisStatus":"complete","samples":10,"stacks":[]}
        """);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(userProfile, false);
        _ = store.Import(kernelProfile, false);
        HistoryRun user = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == userProfile);
        HistoryRun kernel = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == kernelProfile);
        Check(store.Metrics(user.Id, "ipc", null, null).Single().Method == "perf stat derived user"
              && !store.Show(user.Id).Artifacts.Any(x => x.Type == "cpu-stacks"),
            "History indexed a stale stack after failed normalization.");
        using StringWriter output = new();
        TextWriter old = Console.Out;
        Console.SetOut(output);
        try
        {
            _ = await HistoryCommand.ExecuteAsync(["compare", user.Id, kernel.Id, "--db", dbPath, "--json"], default);
        }
        finally
        {
            Console.SetOut(old);
        }

        using JsonDocument comparison = JsonDocument.Parse(output.ToString());
        Check(comparison.RootElement.EnumerateArray().Any(x =>
                x.GetProperty("metric").GetString() == "ipc"
                && x.GetProperty("status").GetString() == "incompatible"),
            "History compared user and kernel IPC as equivalent.");
    }

    string generatedA = Path.Combine(root, "generated-a");
    string generatedB = Path.Combine(root, "generated-b");
    _ = Directory.CreateDirectory(generatedA);
    _ = Directory.CreateDirectory(generatedB);
    Write(generatedA, "run.json", Workflow("succeeded", 1, 2)
        .Replace("bench.json", "/tmp/profile-a.json", StringComparison.Ordinal));
    Write(generatedB, "run.json", Workflow("succeeded", 2, 3)
        .Replace("bench.json", "/tmp/profile-b.json", StringComparison.Ordinal));
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(generatedA, false);
        _ = store.Import(generatedB, false);
        HistoryRun a = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == generatedA);
        HistoryRun b = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == generatedB);
        using StringWriter output = new();
        TextWriter old = Console.Out;
        Console.SetOut(output);
        try
        {
            _ = await HistoryCommand.ExecuteAsync(["compare", a.Id, b.Id, "--db", dbPath, "--json"], default);
        }
        finally
        {
            Console.SetOut(old);
        }

        using JsonDocument parsed = JsonDocument.Parse(output.ToString());
        Check(parsed.RootElement[0].GetProperty("status").GetString() == "unverified",
            "equivalent generated manifests must not be rejected by path");
        Check(parsed.RootElement[0].GetProperty("absoluteChange").ValueKind == JsonValueKind.Null,
            "unverified generated manifests must not emit a delta");
    }

    string workers1 = Path.Combine(root, "workers-1");
    string workers16 = Path.Combine(root, "workers-16");
    _ = Directory.CreateDirectory(workers1);
    _ = Directory.CreateDirectory(workers16);

    static string ProfileReport(string workers) => $$"""
                                                     {"schemaVersion":1,"workflow":"profile.json","manifestHash":"profile-hash",
                                                      "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
                                                      "status":"succeeded",
                                                      "environment":{"hostId":"b813a502b7a447b587f0bf8d24addd27","cpuModel":"Fixture CPU",
                                                        "architecture":"X64","logicalProcessors":16,"availableProcessors":16,
                                                        "physicalPackages":1,"physicalCores":8,"operatingSystem":"Linux",
                                                        "kernelVersion":"6.1","affinity":"0-15","governor":"performance",
                                                        "fingerprint":"host-fingerprint"},
                                                      "buildEnvironments":[{"directory":"/build","configuration":null,
                                                        "generator":"Ninja","compiler":"/usr/bin/clang++","compilerId":"Clang",
                                                        "compilerVersion":"20.0","flags":"-O3","fingerprint":"build-fingerprint"}],
                                                      "nodes":[
                                                        {"id":"timing_main_1","kind":"run","status":"succeeded","buildDirectory":"/build",
                                                         "environmentOverrideHash":"override-base",
                                                         "arguments":["--scenario","idle-burst","--workers","{{workers}}"],
                                                         "processDurationSeconds":2},
                                                        {"id":"capture_main","kind":"profile","status":"succeeded","buildDirectory":"/build",
                                                         "environmentOverrideHash":"override-base",
                                                         "arguments":["--scenario","idle-burst","--workers","{{workers}}"]}]}
                                                     """;

    const string analysisCounters = /*lang=json,strict*/ """
                                    [{"captureId":"capture_main","backend":"perf","mode":"counters",
                                      "counters":{"cycles:u":{"value":1234,"unit":""}}}]
                                    """;
    Write(workers1, "run.json", ProfileReport("1"));
    Write(workers16, "run.json", ProfileReport("16"));
    Write(workers1, "analysis.json", analysisCounters);
    Write(workers16, "analysis.json", analysisCounters);
    using (HistoryStore store = new(dbPath))
    {
        _ = store.Import(workers1, false);
        _ = store.Import(workers16, false);
        HistoryRun a = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == workers1);
        HistoryRun b = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == workers16);
        Check(store.Metrics(a.Id, "cycles:u", "main", "idle-burst").Single().Workload
              != store.Metrics(b.Id, "cycles:u", "main", "idle-burst").Single().Workload,
            "profile workload arguments indexed");
        using StringWriter output = new();
        TextWriter old = Console.Out;
        Console.SetOut(output);
        try
        {
            _ = await HistoryCommand.ExecuteAsync(["compare", a.Id, b.Id, "--db", dbPath, "--json"], default);
        }
        finally
        {
            Console.SetOut(old);
        }

        using JsonDocument parsed = JsonDocument.Parse(output.ToString());
        Check(parsed.RootElement.EnumerateArray().All(x =>
                x.GetProperty("status").GetString() == "incompatible"
                && x.GetProperty("percentChange").ValueKind == JsonValueKind.Null),
            "workers 1 and 16 must never yield percentage changes");
    }

    string workersSame = Path.Combine(root, "workers-same");
    string changedKernel = Path.Combine(root, "changed-kernel");
    string changedHost = Path.Combine(root, "changed-host");
    string changedOverrides = Path.Combine(root, "changed-overrides");
    string drifted = Path.Combine(root, "drifted");
    foreach (string? dir in new[]
             {
                 workersSame, changedKernel, changedHost,
                 changedOverrides, drifted
             })
    {
        _ = Directory.CreateDirectory(dir);
    }

    Write(workersSame, "run.json", ProfileReport("1").Replace("processDurationSeconds\":2",
        "processDurationSeconds\":3", StringComparison.Ordinal));
    Write(changedKernel, "run.json", ProfileReport("1").Replace("host-fingerprint",
        "different-kernel-fingerprint", StringComparison.Ordinal));
    Write(changedHost, "run.json", ProfileReport("1").Replace("b813a502b7a447b587f0bf8d24addd27",
        "f1a438fe4d3742a0931d4e26dc103dab", StringComparison.Ordinal));
    Write(changedOverrides, "run.json", ProfileReport("1").Replace("override-base",
        "override-changed", StringComparison.Ordinal));
    Write(drifted, "run.json", ProfileReport("1").Replace("\"buildEnvironments\"",
        "\"environmentAtEnd\":{\"hostId\":\"b813a502b7a447b587f0bf8d24addd27\",\"fingerprint\":\"drifted\"},\"buildEnvironments\"",
        StringComparison.Ordinal));
    foreach (string? dir in new[]
             {
                 workersSame, changedKernel, changedHost,
                 changedOverrides, drifted
             })
    {
        Write(dir, "analysis.json", analysisCounters);
    }

    using (HistoryStore store = new(dbPath))
    {
        foreach (string? dir in new[]
                 {
                     workersSame, changedKernel, changedHost,
                     changedOverrides, drifted
                 })
        {
            _ = store.Import(dir, false);
        }

        List<HistoryRun> runs = store.Runs(50, null, null, null, null, null);
        HistoryRun baseline = runs.Single(x => x.Directory == workers1);
        Check(baseline.HostId is not null && baseline.EnvironmentFingerprint == "host-fingerprint",
            "host provenance persisted");
        Check(baseline.BuildEnvironments is [{ CompilerId: "Clang", Configuration: null }],
            "build toolchain provenance persisted");

        async Task<JsonDocument> CompareJson(string candidate)
        {
            using StringWriter output = new();
            TextWriter old = Console.Out;
            Console.SetOut(output);
            try
            {
                _ = await HistoryCommand.ExecuteAsync([
                    "compare", baseline.Id,
                    runs.Single(x => x.Directory == candidate).Id, "--db", dbPath, "--json"
                ], default);
            }
            finally
            {
                Console.SetOut(old);
            }

            return JsonDocument.Parse(output.ToString());
        }

        using (JsonDocument same = await CompareJson(workersSame))
        {
            Check(same.RootElement.EnumerateArray().All(x => x.GetProperty("status").GetString() == "compatible"
                                                             && x.GetProperty("reason").GetString()!.Contains(
                                                                 "Descriptive", StringComparison.Ordinal)),
                "matching recorded environment supports descriptive comparison");
        }

        using (JsonDocument kernel = await CompareJson(changedKernel))
        {
            Check(kernel.RootElement.EnumerateArray().All(x => x.GetProperty("status").GetString() == "incompatible"
                                                               && x.GetProperty("percentChange").ValueKind ==
                                                               JsonValueKind.Null),
                "changed environment rejects comparison");
        }

        using (JsonDocument host = await CompareJson(changedHost))
        {
            Check(host.RootElement.EnumerateArray().All(x => x.GetProperty("status").GetString() == "incompatible"),
                "different local hosts reject comparison");
        }

        using (JsonDocument overrides = await CompareJson(changedOverrides))
        {
            Check(
                overrides.RootElement.EnumerateArray().All(x => x.GetProperty("status").GetString() == "incompatible"),
                "changed environment overrides reject comparison");
        }

        using JsonDocument drift = await CompareJson(drifted);
        Check(drift.RootElement.EnumerateArray().All(x => x.GetProperty("status").GetString() == "unverified"
                                                          && x.GetProperty("absoluteChange").ValueKind ==
                                                          JsonValueKind.Null),
            "environment drift suppresses delta");
    }

    static string StatisticalReport(double[] main, string workers = "1", double[]? control = null)
    {
        (string Name, double[] Values)[] samples =
        [
            (Name: "main", Values: main),
            (Name: "control", Values: control ?? [])
        ];
        var nodes = samples.SelectMany(group => group.Values.Select((value, index) => new
        {
            id = $"timing_{group.Name}_{index + 1}",
            kind = "run",
            status = "succeeded",
            buildDirectory = "/build",
            environmentOverrideHash = "override-base",
            arguments = new[] { "--scenario", "idle-burst", "--workers", workers },
            processDurationSeconds = value
        })).ToArray();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            workflow = "statistical-profile.json",
            manifestHash = "same-hash",
            startedAt = "2026-01-01T00:00:00Z",
            finishedAt = "2026-01-01T00:01:00Z",
            status = "succeeded",
            environment = new
            {
                hostId = "b813a502b7a447b587f0bf8d24addd27",
                cpuModel = "Fixture CPU",
                architecture = "X64",
                logicalProcessors = 16,
                availableProcessors = 16,
                physicalPackages = 1,
                physicalCores = 8,
                operatingSystem = "Linux",
                kernelVersion = "6.1",
                affinity = "0-15",
                governor = "performance",
                fingerprint = "host-fingerprint"
            },
            buildEnvironments = new[]
            {
                new
                {
                    directory = "/build", configuration = (string?)null, generator = "Ninja",
                    compiler = "/usr/bin/clang++", compilerId = "Clang",
                    compilerVersion = "20.0", flags = "-O3", fingerprint = "build-fingerprint"
                }
            },
            nodes
        });
    }

    string statisticalBase = Path.Combine(root, "statistical-base");
    string statisticalSlow = Path.Combine(root, "statistical-slow");
    string statisticalEqual = Path.Combine(root, "statistical-equal");
    string statisticalWorkers = Path.Combine(root, "statistical-workers");
    string statisticalShort = Path.Combine(root, "statistical-short");
    string statisticalLarge = Path.Combine(root, "statistical-large");
    foreach (string? dir in new[]
             {
                 statisticalBase, statisticalSlow, statisticalEqual,
                 statisticalWorkers, statisticalShort, statisticalLarge
             })
    {
        _ = Directory.CreateDirectory(dir);
    }

    double[] baseSamples = [.. Enumerable.Range(0, 10).Select(i => 100.0 + (i % 5))];
    double[] slowSamples = [.. Enumerable.Range(0, 10).Select(i => 130.0 + (i % 5))];
    Write(statisticalBase, "run.json", StatisticalReport(baseSamples, control: baseSamples));
    Write(statisticalSlow, "run.json", StatisticalReport(slowSamples, control: baseSamples));
    Write(statisticalEqual, "run.json", StatisticalReport(baseSamples, control: baseSamples));
    Write(statisticalWorkers, "run.json", StatisticalReport(slowSamples, "16", baseSamples));
    Write(statisticalShort, "run.json", StatisticalReport([130, 131], control: [100, 101]));
    Write(statisticalLarge, "run.json", StatisticalReport(
        [.. Enumerable.Range(0, 11).Select(i => 130.0 + (i % 5))]));
    using (HistoryStore store = new(dbPath))
    {
        foreach (string? dir in new[]
                 {
                     statisticalBase, statisticalSlow, statisticalEqual,
                     statisticalWorkers, statisticalShort, statisticalLarge
                 })
        {
            _ = store.Import(dir, false);
        }

        string IdOf(string dir) => store.Runs(50, null, null, null, null, null)
            .Single(x => x.Directory == dir).Id;

        async Task<JsonDocument> DetectJson(string baseline, string candidate,
            params string[] extra)
        {
            using StringWriter output = new();
            TextWriter previous = Console.Out;
            Console.SetOut(output);
            try
            {
                Check(await HistoryCommand.ExecuteAsync([
                    "detect", IdOf(baseline),
                    IdOf(candidate), "--db", dbPath, "--metric", "process_wall_time",
                    "--direction", "higher-worse", "--min-percent", "10", "--json",
                    .. extra
                ], default) == 0, "detector command");
            }
            finally
            {
                Console.SetOut(previous);
            }

            return JsonDocument.Parse(output.ToString());
        }

        using (JsonDocument detected = await DetectJson(statisticalBase, statisticalSlow))
        {
            Check(detected.RootElement.GetProperty("schemaVersion").GetInt32() == 1,
                "detector JSON version");
            JsonElement[] rows = [.. detected.RootElement.GetProperty("results").EnumerateArray()];
            JsonElement main = rows.Single(x => x.GetProperty("variant").GetString() == "main");
            Check(main.GetProperty("status").GetString() == "regression"
                  && main.GetProperty("exact").GetBoolean()
                  && main.GetProperty("permutations").GetInt32() == 184756
                  && main.GetProperty("adjustedPValue").GetDouble()
                  >= main.GetProperty("pValue").GetDouble(),
                "exact permutation detector and Holm correction");
            Check(rows.Single(x => x.GetProperty("variant").GetString() == "control")
                    .GetProperty("status").GetString() == "inconclusive",
                "unchanged control must not be a regression");
        }

        using (JsonDocument equal = await DetectJson(statisticalBase, statisticalEqual,
                   "--variant", "main"))
        {
            Check(equal.RootElement.GetProperty("results")[0].GetProperty("status").GetString()
                  == "inconclusive", "equal samples detector");
        }

        using (JsonDocument incompatible = await DetectJson(statisticalBase, statisticalWorkers))
        {
            Check(incompatible.RootElement.GetProperty("results").EnumerateArray().All(x =>
                    x.GetProperty("status").GetString() == "incompatible"
                    && x.GetProperty("pValue").ValueKind == JsonValueKind.Null),
                "detector rejected different workload arguments");
        }

        using (JsonDocument shortRun = await DetectJson(statisticalShort, statisticalSlow,
                   "--variant", "main"))
        {
            Check(shortRun.RootElement.GetProperty("results")[0].GetProperty("status").GetString()
                  == "insufficient", "detector rejected two samples");
        }

        using (JsonDocument large = await DetectJson(statisticalBase, statisticalLarge,
                   "--variant", "main"))
        {
            Check(!large.RootElement.GetProperty("results")[0].GetProperty("exact").GetBoolean()
&& large.RootElement.GetProperty("results")[0]
                    .GetProperty("permutations").GetInt32() == 9999,
                "deterministic Monte Carlo path");
        }
        using
                StringWriter output = new();
        TextWriter previous = Console.Out;
        Console.SetOut(output);
        try
        {
            Check(await HistoryCommand.ExecuteAsync([
                    "detect", IdOf(statisticalBase),
                    IdOf(statisticalSlow), "--db", dbPath, "--metric", "process_wall_time",
                    "--direction", "lower-worse", "--min-percent", "10", "--json"
                ], default) == 0,
                "reversed direction command");
        }
        finally
        {
            Console.SetOut(previous);
        }

        using JsonDocument reversed = JsonDocument.Parse(output.ToString());
        Check(reversed.RootElement.GetProperty("results").EnumerateArray().All(x =>
                x.GetProperty("status").GetString() != "regression"),
            "regression direction must be explicit");
    }

    using (HistoryStore rebuilt = new(Path.Combine(root, "history-rebuilt.sqlite")))
    {
        _ = rebuilt.Import(resultRoot, true);
        List<HistoryRun> rebuiltRows = rebuilt.Runs(50, null, null, null, null, null);
        Check(rebuiltRows.Count == 2, "reconstruction count " + rebuiltRows.Count);
    }

    string oldPath = Path.Combine(root, "history-v1.sqlite");
    using (HistoryStore initial = new(oldPath))
    {
        _ = initial.Import(success, false);
    }

    using (SqliteConnection old = new(new SqliteConnectionStringBuilder
    { DataSource = oldPath, Pooling = false }.ToString()))
    {
        await old.OpenAsync();
        using SqliteCommand command = old.CreateCommand();
        command.CommandText = """
                              ALTER TABLE runs DROP COLUMN build_environment_json;
                              ALTER TABLE runs DROP COLUMN host_id;
                              ALTER TABLE runs DROP COLUMN environment_fingerprint;
                              ALTER TABLE runs DROP COLUMN environment_json;
                              ALTER TABLE runs DROP COLUMN indexer_version;
                              PRAGMA user_version=1;
                              """;
        _ = await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name='indexer_version'";
        Check(Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.CurrentCulture) == 0, "v1 fixture column removal");
    }

    using (HistoryStore upgraded = new(oldPath))
    {
        Check(upgraded.Import(success, false).Contains("Imported 1", StringComparison.Ordinal), "v1 migration");
        Check(upgraded.Import(success, false).Contains("unchanged 1", StringComparison.Ordinal), "v4 indexer version");
        Check(upgraded.Runs(50, null, null, null, null, null).Count == 1,
            "v1 migration preserved stable run identity");
    }

    string v2Path = Path.Combine(root, "history-v2.sqlite");
    using (HistoryStore initial = new(v2Path))
    {
        _ = initial.Import(success, false);
    }

    using (SqliteConnection old = new(new SqliteConnectionStringBuilder
    { DataSource = v2Path, Pooling = false }.ToString()))
    {
        await old.OpenAsync();
        using SqliteCommand command = old.CreateCommand();
        command.CommandText = """
                              ALTER TABLE runs DROP COLUMN build_environment_json;
                              ALTER TABLE runs DROP COLUMN host_id;
                              ALTER TABLE runs DROP COLUMN environment_fingerprint;
                              ALTER TABLE runs DROP COLUMN environment_json;
                              UPDATE runs SET indexer_version=2;
                              PRAGMA user_version=2;
                              """;
        _ = await command.ExecuteNonQueryAsync();
    }

    using (HistoryStore upgraded = new(v2Path))
    {
        Check(upgraded.Import(success, false).Contains("Imported 1", StringComparison.Ordinal), "v2 migration");
        Check(upgraded.Runs(50, null, null, null, null, null).Count == 1,
            "v2 migration preserved run");
    }

    // A workflow imports only explicitly captured harness snapshots, not arbitrary
    // mutable files from a CMake build tree.
    string harnessRun = Path.Combine(root, "dagflow-harness-workflow");
    string harnessArtifacts = Path.Combine(harnessRun, "harness", "api");
    _ = Directory.CreateDirectory(harnessArtifacts);
    Write(harnessArtifacts, "manifest.json", /*lang=json,strict*/ "{\"schemaVersion\":1}");

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
    string harnessHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(harnessArtifacts, "manifest.json"), CancellationToken.None))).ToLowerInvariant();
#pragma warning restore CA1308

    Write(harnessArtifacts, "summary.json", /*lang=json,strict*/ """
                                            {"status":"passed","manifest_sha256":"hash","cases":{
                                              "chain-w2":{"metrics":{"run_p50_us":{"scale":1000000,"count":2,"median":1500000}}}}}
                                            """.Replace("hash", harnessHash, StringComparison.Ordinal));
    Write(harnessArtifacts, "runs.jsonl", string.Join("\n", HarnessRunRows).Replace("hash", harnessHash, StringComparison.Ordinal));
    Write(harnessRun, "run.json", /*lang=json,strict*/ """
                                  {"schemaVersion":1,"workflow":"dagflow.json","manifestHash":"wf",
                                   "status":"succeeded","startedAt":"2026-01-01T00:00:00Z",
                                   "finishedAt":"2026-01-01T00:05:00Z","nodes":[
                                   {"id":"api","kind":"invoke","status":"succeeded","dependencies":[],
                                    "buildDirectory":"/build","configuration":"Release","target":"run_dagflow_api_campaign",
                                    "outputs":{"dagflow-manifest.json":"harness/api/manifest.json",
                                               "dagflow-summary.json":"harness/api/summary.json",
                                               "dagflow-runs.jsonl":"harness/api/runs.jsonl"}}]}
                                  """);
    using (HistoryStore store = new(dbPath))
    {
        Check(store.Import(harnessRun, false).Contains("Imported 1", StringComparison.Ordinal), "harness import");
        string runId = store.Runs(50, null, null, null, "dagflow", null).Single().Id;
        List<HistoryMeasurement> samples = store.Metrics(runId, "run_p50_us", null, "chain-w2");
        Check(samples.Count == 2 && samples.Select(x => x.Value).SequenceEqual([1, 2])
                                 && samples.All(x => x.Method == "dagflow-harness" && x.NodeId == "api"),
            "harness samples indexed and attributed");
        Check(store.Show(runId).Artifacts.Count(x => x.Type.StartsWith("dagflow-", StringComparison.CurrentCulture)) == 3,
            "harness snapshot artifacts");
        Check(store.Import(harnessRun, false).Contains("unchanged 1", StringComparison.Ordinal), "harness idempotence");
        // Legacy generic harness summaries omit manifest_sha256 entirely.
        string summaryFile = Path.Combine(harnessArtifacts, "summary.json");
        await File.WriteAllTextAsync(summaryFile, (await File.ReadAllTextAsync(summaryFile, CancellationToken.None)).Replace(
            "\"manifest_sha256\":\"" + harnessHash + "\",", "", StringComparison.Ordinal), CancellationToken.None);
        Check(store.Import(harnessRun, false).Contains("Imported 1", StringComparison.Ordinal), "legacy harness summary import");
        Check(store.Metrics(runId, "run_p50_us", null, "chain-w2").Count == 2,
            "legacy harness samples survived reindex");
        Write(harnessArtifacts, "runs.jsonl", "{}\n");
        try
        {
            _ = store.Import(harnessRun, false);
            throw new System.Diagnostics.UnreachableException("corrupt harness accepted");
        }
        catch (InvalidDataException)
        {
            /* Corruption must not be silently imported. */
        }
    }

    Console.WriteLine("History checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

internal sealed partial class Program
{
    private static readonly string[] HarnessRunRows =
    [
        /*lang=json,strict*/
        """{"case":"chain-w2","warmup":true,"result":"0","output":{"run_p50_us":999}}""",
        /*lang=json,strict*/
        """{"case":"chain-w2","warmup":false,"result":"0","manifest_sha256":"hash","output":{"run_p50_us":1}}""",
        /*lang=json,strict*/
        """{"case":"chain-w2","warmup":false,"result":"0","manifest_sha256":"hash","output":{"run_p50_us":2}}"""
    ];
}
