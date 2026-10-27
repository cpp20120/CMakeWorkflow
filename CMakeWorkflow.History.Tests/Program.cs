using System.Text.Json;
using System.Security.Cryptography;
using CMakeWorkflow.History;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "cmake-workflow-history-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var dbPath = Path.Combine(root, "history.sqlite");
    var resultRoot = Path.Combine(root, "results");
    var success = Path.Combine(resultRoot, "success");
    var failure = Path.Combine(resultRoot, "failure");
    var active = Path.Combine(resultRoot, "active");
    Directory.CreateDirectory(success);
    Directory.CreateDirectory(failure);
    Directory.CreateDirectory(active);
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); }
    static void Write(string dir, string file, string text) =>
        File.WriteAllText(Path.Combine(dir, file), text);
    static string Workflow(string status, double first, double second, string unit = "s") => $$"""
        {"schemaVersion":1,"workflow":"bench.json","manifestHash":"abc",
         "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
         "status":"{{status}}","nodes":[
           {"id":"build","kind":"build","status":"succeeded","dependencies":[],"buildDirectory":"/build"},
           {"id":"timing_main_1","kind":"run","status":"succeeded","dependencies":["build"],
            "arguments":["--scenario","idle"],"processDurationSeconds":{{first}}},
           {"id":"timing_main_2","kind":"run","status":"succeeded","dependencies":["build"],
            "arguments":["--scenario","idle"],"processDurationSeconds":{{second}}}]}
        """;
    Write(success, "run.json", Workflow("succeeded", 2, 4));
    Write(failure, "run.json", Workflow("failed", 4, 6));
    Write(active, "run.json", """{"schemaVersion":1,"workflow":"active","status":"running","nodes":[]}""");
    using (var store = new HistoryStore(dbPath))
    {
        Check(store.Runs(50, null, null, null, null, null).Count == 0, "fresh DB");
        Check(store.Import(resultRoot, true).Contains("Imported 2"), "completed and failed import");
        Check(store.Runs(50, "failed", null, null, null, null).Count == 1, "status filter");
        Check(store.Runs(50, null, "2026-01-02T00:00:00Z", null, null, null).Count == 0,
            "time filter");
        Check(store.Runs(50, null, null, null, "bench", "main").Count == 2,
            "workflow and variant filters");
        Check(store.Runs(50, null, null, null, null, null).Count == 2, "active skipped");
        Check(store.Import(resultRoot, true).Contains("unchanged 2"), "idempotent import");
        Check(store.Import(Path.Combine(success, "run.json"), false).Contains("unchanged 1"),
            "report file path");
        var row = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == success);
        var measurements = store.Metrics(row.Id, "process_wall_time", "main", null);
        Check(measurements.Count == 2 && measurements.Select(x => x.Value).SequenceEqual([2, 4])
            && measurements.Select(x => x.SampleIndex).SequenceEqual([0, 1]), "raw samples");
        Check(store.Show(row.Id).Nodes.Count == 3, "nodes and dependencies");
        Write(success, "run.json", Workflow("succeeded", 3, 5));
        Check(store.Import(success, false).Contains("Imported 1"), "changed report refresh");
        Check(store.Metrics(row.Id, null, null, null).Count == 2
            && store.Metrics(row.Id, null, null, null)[0].Value == 3, "no duplicate samples");
    }
    using (var reopened = new HistoryStore(dbPath))
    {
        Check(reopened.Runs(50, null, null, null, null, null).Count == 2, "reopen persistence");
        var rows = reopened.Runs(50, null, null, null, null, null);
        var output = new StringWriter();
        var old = Console.Out;
        Console.SetOut(output);
        try
        {
            Check(await HistoryCommand.ExecuteAsync(["compare", rows.Single(x => x.Directory == success).Id,
                rows.Single(x => x.Directory == failure).Id, "--db", dbPath, "--json"], default) == 0,
                "comparison command");
        }
        finally { Console.SetOut(old); }
        using var json = JsonDocument.Parse(output.ToString());
        var comparison = json.RootElement[0];
        Check(comparison.GetProperty("status").GetString() == "unverified"
            && comparison.GetProperty("baseline").GetProperty("median").GetDouble() == 4
            && comparison.GetProperty("candidate").GetProperty("median").GetDouble() == 5
            && comparison.GetProperty("percentChange").GetDouble() == 25, "descriptive comparison");
        output.GetStringBuilder().Clear();
        Console.SetOut(output);
        try
        {
            Check(await HistoryCommand.ExecuteAsync(["runs", "--db", dbPath, "--json"], default) == 0, "runs JSON");
        }
        finally { Console.SetOut(old); }
        Check(JsonDocument.Parse(output.ToString()).RootElement.GetArrayLength() == 2, "JSON output");
    }
    var bad = Path.Combine(root, "bad");
    Directory.CreateDirectory(bad);
    Write(bad, "run.json", """{"schemaVersion":9,"workflow":"bad","status":"failed","nodes":[]}""");
    using (var store = new HistoryStore(dbPath))
    {
        try { store.Import(bad, false); throw new Exception("unsupported accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("schemaVersion"), "unsupported diagnostics"); }
        Write(bad, "run.json", "{");
        try { store.Import(bad, false); throw new Exception("malformed accepted"); }
        catch (JsonException) { }
        var batch = Path.Combine(root, "batch");
        var good = Path.Combine(batch, "a-good");
        var corrupt = Path.Combine(batch, "z-corrupt");
        Directory.CreateDirectory(good);
        Directory.CreateDirectory(corrupt);
        Write(good, "run.json", Workflow("succeeded", 1, 1));
        Write(corrupt, "run.json", "{");
        var before = store.Runs(50, null, null, null, null, null).Count;
        try { store.Import(batch, true); throw new Exception("partial batch accepted"); }
        catch (JsonException) { }
        Check(store.Runs(50, null, null, null, null, null).Count == before, "atomic batch");
    }
    var captureOnly = Path.Combine(root, "capture-only");
    Directory.CreateDirectory(captureOnly);
    Write(captureOnly, "cpu.results.json", """
        {"captureId":"cpu","backend":"etw","mode":"cpu","status":"complete",
         "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
         "rawTrace":"traces/raw/cpu.etl","rawTraceSha256":"abc",
         "eventSource":"ETW CPU sampling"}
        """);
    Directory.CreateDirectory(Path.Combine(captureOnly, "traces", "normalized"));
    Write(Path.Combine(captureOnly, "traces", "normalized"), "cpu.etw.json", """
        {"schemaVersion":1,"backend":"etw","attribution":"target PID and time window",
         "targetCpuSamples":5,"readyLatencySumMilliseconds":2.5}
        """);
    using (var store = new HistoryStore(dbPath))
    {
        Check(store.Import(captureOnly, false).Contains("Imported 1"), "standalone capture");
        var capture = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == captureOnly);
        Check(capture.Status == "succeeded"
            && store.Metrics(capture.Id, "targetCpuSamples", null, null).Single().Value == 5
            && store.Show(capture.Id).Artifacts.Any(x => x.Type == "trace" && !x.Available),
            "ETW summary and missing trace link");
    }
    var fuzz = Path.Combine(root, "fuzz");
    Directory.CreateDirectory(fuzz);
    Write(fuzz, "report.json", """
        {"schemaVersion":1,"layer":"ranges","backend":"libfuzzer",
         "startedAt":"2026-01-01T00:00:00Z","finishedAt":"2026-01-01T00:01:00Z",
         "status":"failed","executable":"/build/fuzzer","executableSha256":"abc",
         "crashArtifacts":["artifacts/crash-1"]}
        """);
    using (var store = new HistoryStore(dbPath))
    {
        store.Import(fuzz, false);
        var row = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == fuzz);
        Check(row.Format == "fuzz" && row.Status == "failed"
            && store.Show(row.Id).Measurements.Count == 0
            && store.Show(row.Id).Artifacts.Any(x => x.Type == "crash"), "fuzz report and missing metrics");
    }
    var campaign = Path.Combine(root, "campaign");
    Directory.CreateDirectory(campaign);
    Write(campaign, "report.json", """{"checks":{"parking":{"status":"PASS"}}}""");
    using (var store = new HistoryStore(dbPath))
        Check(store.Import(campaign, false).Contains("skipped 1"), "incomplete campaign skipped");
    var reportHash = Convert.ToHexString(SHA256.HashData(
        File.ReadAllBytes(Path.Combine(campaign, "report.json")))).ToLowerInvariant();
    Write(campaign, "campaign-completion.json", $$"""
        {"schemaVersion":1,"campaign":"weak-memory","report":"report.json",
         "reportSha256":"{{reportHash}}","startedAt":"2026-01-01T00:00:00Z",
         "finishedAt":"2026-01-01T00:01:00Z","status":"succeeded"}
        """);
    using (var store = new HistoryStore(dbPath))
    {
        store.Import(campaign, false);
        var row = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == campaign);
        Check(row.Format == "campaign" && row.Workflow == "weak-memory"
            && store.Show(row.Id).Artifacts.Count == 2, "completed generic campaign");
        Write(campaign, "report.json", """{"checks":{}}""");
        try { store.Import(campaign, false); throw new Exception("campaign hash drift accepted"); }
        catch (InvalidDataException) { }
    }
    var profile = Path.Combine(root, "profile");
    Directory.CreateDirectory(profile);
    Write(profile, "run.json", Workflow("succeeded", 1, 2));
    Write(profile, "counter-comparison.json", """
        {"schemaVersion":1,"backend":"perf stat","variants":[
        {"name":"main","events":{"cycles":{"value":100,"unit":"count"}}}]}
        """);
    var profile2 = Path.Combine(root, "profile2");
    Directory.CreateDirectory(profile2);
    Write(profile2, "run.json", Workflow("succeeded", 2, 3));
    Write(profile2, "counter-comparison.json", """
        {"schemaVersion":1,"backend":"perf stat","variants":[
        {"name":"main","events":{"cycles":{"value":110,"unit":"ms"}}}]}
        """);
    using (var store = new HistoryStore(dbPath))
    {
        store.Import(profile, false);
        store.Import(profile2, false);
        var first = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == profile);
        var second = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == profile2);
        Check(store.Metrics(first.Id, "cycles", null, null).Single().Value == 100, "counter import");
        var output = new StringWriter(); var old = Console.Out; Console.SetOut(output);
        try { await HistoryCommand.ExecuteAsync(["compare", first.Id, second.Id, "--db", dbPath, "--json"], default); }
        finally { Console.SetOut(old); }
        using var doc = JsonDocument.Parse(output.ToString());
        Check(doc.RootElement.EnumerateArray().Any(x => x.GetProperty("metric").GetString() == "cycles"
            && x.GetProperty("status").GetString() == "incompatible"), "unit compatibility");
        Write(profile, "counter-comparison.json", """
            {"schemaVersion":1,"backend":"ETW","variants":[
            {"name":"main","events":{"cycles":{"value":100,"unit":"ms"}}}]}
            """);
        Check(store.Import(profile, false).Contains("Imported 1"), "small metadata refresh");
        output.GetStringBuilder().Clear(); Console.SetOut(output);
        try { await HistoryCommand.ExecuteAsync(["compare", first.Id, second.Id, "--db", dbPath, "--json"], default); }
        finally { Console.SetOut(old); }
        using var methodDoc = JsonDocument.Parse(output.ToString());
        Check(methodDoc.RootElement.EnumerateArray().Any(x => x.GetProperty("metric").GetString() == "cycles"
            && x.GetProperty("status").GetString() == "incompatible"), "method compatibility");
        Directory.Delete(profile, true);
        Check(!store.Show(first.Id).Run.SourceAvailable, "deleted source explicit");
    }
    var singleProfile = Path.Combine(root, "single-profile");
    Directory.CreateDirectory(singleProfile);
    Write(singleProfile, "run.json", Workflow("succeeded", 1, 2));
    Write(singleProfile, "analysis.json", """
        [{"captureId":"capture_main","backend":"perf","mode":"counters",
          "counters":{"cycles:u":{"value":1234,"unit":""},
                      "instructions:u":{"value":5678,"unit":""}}}]
        """);
    using (var store = new HistoryStore(dbPath))
    {
        store.Import(singleProfile, false);
        var id = store.Runs(50, null, null, null, null, null)
            .Single(x => x.Directory == singleProfile).Id;
        Check(store.Metrics(id, "cycles:u", "main", null).Single().Value == 1234,
            "single-profile analysis counters");
    }
    var generatedA = Path.Combine(root, "generated-a");
    var generatedB = Path.Combine(root, "generated-b");
    Directory.CreateDirectory(generatedA);
    Directory.CreateDirectory(generatedB);
    Write(generatedA, "run.json", Workflow("succeeded", 1, 2)
        .Replace("bench.json", "/tmp/profile-a.json", StringComparison.Ordinal));
    Write(generatedB, "run.json", Workflow("succeeded", 2, 3)
        .Replace("bench.json", "/tmp/profile-b.json", StringComparison.Ordinal));
    using (var store = new HistoryStore(dbPath))
    {
        store.Import(generatedA, false);
        store.Import(generatedB, false);
        var a = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == generatedA);
        var b = store.Runs(50, null, null, null, null, null).Single(x => x.Directory == generatedB);
        var output = new StringWriter(); var old = Console.Out; Console.SetOut(output);
        try { await HistoryCommand.ExecuteAsync(["compare", a.Id, b.Id, "--db", dbPath, "--json"], default); }
        finally { Console.SetOut(old); }
        using var parsed = JsonDocument.Parse(output.ToString());
        Check(parsed.RootElement[0].GetProperty("status").GetString() == "unverified",
            "equivalent generated manifests must not be rejected by path");
    }
    using (var rebuilt = new HistoryStore(Path.Combine(root, "history-rebuilt.sqlite")))
    {
        rebuilt.Import(resultRoot, true);
        var rebuiltRows = rebuilt.Runs(50, null, null, null, null, null);
        Check(rebuiltRows.Count == 2, "reconstruction count " + rebuiltRows.Count);
    }
    var oldPath = Path.Combine(root, "history-v1.sqlite");
    using (var initial = new HistoryStore(oldPath))
        initial.Import(success, false);
    using (var old = new SqliteConnection($"Data Source={oldPath}"))
    {
        old.Open();
        using var command = old.CreateCommand();
        command.CommandText = """
            ALTER TABLE runs DROP COLUMN indexer_version;
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name='indexer_version'";
        Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "v1 fixture column removal");
    }
    using (var upgraded = new HistoryStore(oldPath))
    {
        Check(upgraded.Import(success, false).Contains("Imported 1"), "v1 migration");
        Check(upgraded.Import(success, false).Contains("unchanged 1"), "v2 indexer version");
        Check(upgraded.Runs(50, null, null, null, null, null).Count == 1,
            "v1 migration preserved stable run identity");
    }
    Console.WriteLine("History checks passed.");
}
finally { Directory.Delete(root, recursive: true); }
