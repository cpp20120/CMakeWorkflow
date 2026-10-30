using CMakeWorkflow;
using CMakeWorkflow.Profiling;
using CMakeWorkflow.Profiling.Backends;

var root = Path.Combine(Path.GetTempPath(), "cmake-workflow-profile-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var executable = Path.Combine(root, "test-worker");
    await File.WriteAllTextAsync(executable, "fixture");
    var request = new ProfileRequest(executable, ["--workers", "1"], root,
        new Dictionary<string, string>(), root, "capture", "cpu", TimeSpan.FromSeconds(5));

    using (var script = new StringReader("""
        test 100 [001] 123.456: 1 cycles:u:
                     7ff0 Work::execute (dagflow-runtime-bench)
                     7ff1 Pool::worker (dagflow-runtime-bench)
        test 100 [001] 123.457: 1 cycles:u:
                     7ff0 Work::execute (dagflow-runtime-bench)
                     7ff1 Pool::worker (dagflow-runtime-bench)
        """))
    {
        var stacks = PerfStackAnalyzer.Parse(script, CancellationToken.None).ToJson("perf", "capture");
        Check(stacks["samples"]!.GetValue<int>() == 2
            && stacks["symbolizedSamples"]!.GetValue<int>() == 2
            && stacks["stacks"]!.AsArray()[0]!["count"]!.GetValue<int>() == 2,
            "perf script folded stack parsing or normalization failed.");
    }
    using (var script = new StringReader("""
        cmd 123 [003] 123.001234: sched:sched_wakeup: comm=worker pid=42 prio=120 target_cpu=003
        cmd 123 [003] 123.002345: sched:sched_switch: prev_comm=worker prev_pid=42 prev_prio=120 prev_state=S ==> next_comm=other next_pid=43 next_prio=120
        """))
    using (var output = new StringWriter())
    {
        Check(PerfSchedulerAnalyzer.Parse(script, output, CancellationToken.None) == 3
            && output.ToString().Contains("\"ready\"")
            && output.ToString().Contains("\"offcpu\"")
            && output.ToString().Contains("\"running\"")
            && output.ToString().Contains("perf-relative"),
            "perf scheduler tracepoint normalization failed.");
    }
    var perf = new FakeProcess();
    var perfResult = await new LinuxPerfBackend(perf).CaptureAsync(request, CancellationToken.None);
    Check(perf.Calls.Count == 1 && perf.Calls[0].Executable == "perf"
        && perf.Calls[0].Args.Take(2).SequenceEqual(["record", "-o"])
        && perf.Calls[0].Args.Contains("--call-graph")
        && perf.Calls[0].Args.TakeLast(3).SequenceEqual([executable, "--workers", "1"])
        && File.Exists(perfResult.RawTrace), "perf capture invocation or artifact");
    var counterResult = await new LinuxPerfBackend(perf).CaptureAsync(
        request with { CaptureId = "counters", Mode = "counters" }, CancellationToken.None);
    Check(perf.Calls[1].Args.Take(3).SequenceEqual(["stat", "-x", ","])
        && counterResult.RawTrace.EndsWith(".csv", StringComparison.Ordinal)
        && File.Exists(counterResult.RawTrace), "perf counter capture must remain separate from sampling");

    // No profiler tool is invoked by replay when a stored executable is
    // stale or both variants point to the same mutable build output.
    var replayExe = Path.Combine(root, "replay-executable");
    await File.WriteAllTextAsync(replayExe, "immutable-benchmark");
    var replayHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        await File.ReadAllBytesAsync(replayExe)));
    var replayDir = Path.Combine(root, "replay-fixture");
    Directory.CreateDirectory(replayDir);
    var report = new System.Text.Json.Nodes.JsonObject
    {
        ["status"] = "succeeded",
        ["nodes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "sample", ["kind"] = "run", ["status"] = "succeeded",
            ["artifact"] = new System.Text.Json.Nodes.JsonObject
            {
                ["executable"] = replayExe, ["sha256"] = replayHash
            },
            ["environmentOverrideHash"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["arguments"] = new System.Text.Json.Nodes.JsonArray(
                System.Text.Json.Nodes.JsonValue.Create("--scenario"),
                System.Text.Json.Nodes.JsonValue.Create("chain")),
            ["workingDirectory"] = root
        })
    };
    await File.WriteAllTextAsync(Path.Combine(replayDir, "run.json"), report.ToJsonString());
    try
    {
        await ProfileReplayCommand.ExecuteAsync(["replay", replayDir, replayDir, "--node", "sample"], CancellationToken.None);
        throw new Exception("Expected same-binary replay rejection");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("same mutable executable", StringComparison.Ordinal)) { }
    await File.WriteAllTextAsync(replayExe, "changed-binary");
    try
    {
        await ProfileReplayCommand.ExecuteAsync(["replay", replayDir, replayDir, "--node", "sample"], CancellationToken.None);
        throw new Exception("Expected stale SHA rejection");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("changed since its original run", StringComparison.Ordinal)) { }

    // Native harness case replay must use snapshotted executable bytes, not
    // mutable CMake target paths; unsupported/ambiguous provenance fails closed.
    var nativeA = Path.Combine(root, "harness-before");
    var nativeB = Path.Combine(root, "harness-after");
    foreach (var dir in new[] { nativeA, nativeB })
    {
        var artifacts = Path.Combine(dir, "harness", "api-workloads");
        Directory.CreateDirectory(artifacts);
        var preserved = Path.Combine(artifacts, OperatingSystem.IsWindows() ? "replay-binary.exe" : "replay-binary");
        await File.WriteAllTextAsync(preserved, dir);
        var binarySha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(preserved)));
        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["binary"] = Path.Combine(dir, "mutable-old-binary"),
            ["binary_sha256"] = binarySha,
            ["cases"] = new System.Text.Json.Nodes.JsonObject
            {
                ["chain-4"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["args"] = new System.Text.Json.Nodes.JsonArray(
                        System.Text.Json.Nodes.JsonValue.Create("--scenario"),
                        System.Text.Json.Nodes.JsonValue.Create("chain")),
                    ["metadata"] = new System.Text.Json.Nodes.JsonObject()
                }
            },
            ["replayCases"] = new System.Text.Json.Nodes.JsonObject
            {
                ["chain-4"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["schemaVersion"] = 1, ["replayable"] = true,
                    ["arguments"] = new System.Text.Json.Nodes.JsonArray(
                        System.Text.Json.Nodes.JsonValue.Create("--scenario"),
                        System.Text.Json.Nodes.JsonValue.Create("chain")),
                    ["workingDirectory"] = root, ["cpuSet"] = ""
                }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "manifest.json"), manifest.ToJsonString());
        var fileName = Path.GetFileName(preserved);
        var reportHarness = new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = "succeeded",
            ["nodes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "api-workloads", ["kind"] = "invoke", ["status"] = "succeeded",
                ["outputs"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["dagflow-manifest.json"] = "harness/api-workloads/manifest.json",
                    ["dagflow-replay-binary"] = "harness/api-workloads/" + fileName
                }
            })
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "run.json"), reportHarness.ToJsonString());
    }
    var harnessArgs = new[] { "replay", nativeA, nativeB, "--node", "api-workloads", "--case", "chain-4" };
    // These rejection tests execute before any profiler is launched.
    try
    {
        await ProfileReplayCommand.ExecuteAsync(["replay", nativeA, nativeB, "--node", "api-workloads", "--case", "missing"], CancellationToken.None);
        throw new Exception("Expected missing native case rejection");
    }
    catch (InvalidDataException e) when (e.Message.Contains("no native replay descriptor", StringComparison.Ordinal)) { }
    var tampered = Path.Combine(nativeA, "harness", "api-workloads",
        OperatingSystem.IsWindows() ? "replay-binary.exe" : "replay-binary");
    await File.AppendAllTextAsync(tampered, "tamper");
    try
    {
        await ProfileReplayCommand.ExecuteAsync(harnessArgs, CancellationToken.None);
        throw new Exception("Expected preserved-binary SHA rejection");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("Snapshotted benchmark binary changed", StringComparison.Ordinal)) { }
    // Keep baseline bytes valid, but reject a non-replayable case explicitly.
    await File.WriteAllTextAsync(tampered, nativeA);
    var candidateManifest = Path.Combine(nativeB, "harness", "api-workloads", "manifest.json");
    var candidateObject = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(candidateManifest))!.AsObject();
    candidateObject["replayCases"]!["chain-4"]!["replayable"] = false;
    candidateObject["replayCases"]!["chain-4"]!["reason"] = "environment overrides";
    await File.WriteAllTextAsync(candidateManifest, candidateObject.ToJsonString());
    try
    {
        await ProfileReplayCommand.ExecuteAsync(harnessArgs, CancellationToken.None);
        throw new Exception("Expected unsupported native harness case rejection");
    }
    catch (InvalidDataException e) when (e.Message.Contains("environment overrides", StringComparison.Ordinal)) { }

    var instruments = new FakeProcess();
    var macResult = await new MacInstrumentsBackend(instruments).CaptureAsync(request, CancellationToken.None);
    Check(instruments.Calls.Count == 1 && instruments.Calls[0].Executable == "xcrun"
        && instruments.Calls[0].Args.Take(2).SequenceEqual(["xctrace", "record"])
        && instruments.Calls[0].Args.Contains("--launch")
        && Directory.Exists(macResult.RawTrace), "xctrace capture invocation or artifact");
    var emptyTraceHash = await ProfileArtifactHash.Sha256Async(macResult.RawTrace, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(macResult.RawTrace, "trace.db"), "fixture");
    Check(emptyTraceHash != await ProfileArtifactHash.Sha256Async(macResult.RawTrace, CancellationToken.None),
        "Instruments directory artifact hash ignored contents.");

    var wpr = new FakeProcess();
    var etwResult = await new WindowsEtwBackend(wpr).CaptureAsync(request, CancellationToken.None);
    Check(wpr.Calls.Select(x => x.Executable).SequenceEqual(["wpr", "wpr", executable, "wpr"]),
        "WPR must start before launching the target and stop afterward");
    Check(wpr.Calls[1].Args.Take(3).SequenceEqual(["-start", "CPU.light", "-filemode"])
        && wpr.Calls[3].Args[0] == "-stop"
        && wpr.Calls.Where(x => x.Executable == "wpr").All(x => x.Args[^2] == "-instancename")
        && File.Exists(etwResult.RawTrace) && etwResult.TargetProcessId == 42
        && etwResult.TargetStartedAt is not null && etwResult.TargetFinishedAt is not null,
        "WPR named session, target identity or ETL artifact");

    var etwXml = Path.Combine(root, "events.xml");
    await File.WriteAllTextAsync(etwXml, """
        <Events xmlns="https://schemas.microsoft.com/win/2004/08/events/event">
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:00Z"/><Execution ProcessID="1" ThreadID="1"/></System><EventData><Data Name="ProcessId">0x2A</Data><Data Name="ImageFileName">test-worker</Data></EventData><RenderingInfo><EventName>Process</EventName><Opcode>Start</Opcode></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:01Z"/><Execution ProcessID="1" ThreadID="1"/></System><EventData><Data Name="ProcessId">42</Data><Data Name="TThreadId">100</Data></EventData><RenderingInfo><EventName>Thread</EventName><Opcode>Start</Opcode></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:02Z"/><Execution ProcessID="1" ThreadID="1" ProcessorID="2"/></System><EventData><Data Name="ThreadId">100</Data><Data Name="InstructionPointer">0x1234</Data></EventData><ExtendedData><StackTrace>0x1234 0x5678</StackTrace></ExtendedData><RenderingInfo><EventName>SampleProfile</EventName></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:02Z"/><Execution ProcessID="99" ThreadID="200"/></System><RenderingInfo><EventName>SampledProfile</EventName></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:02.5Z"/></System><EventData><Data Name="TThreadId">100</Data></EventData><RenderingInfo><EventName>ReadyThread</EventName></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:03Z"/></System><EventData><Data Name="NewThreadId">100</Data><Data Name="OldThreadId">200</Data></EventData><RenderingInfo><EventName>CSwitch</EventName></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:04Z"/></System><EventData><Data Name="NewThreadId">200</Data><Data Name="OldThreadId">100</Data></EventData><RenderingInfo><EventName>CSwitch</EventName></RenderingInfo></Event>
          <Event><System><TimeCreated SystemTime="2026-10-26T10:00:06Z"/><Execution ProcessID="42" ThreadID="100"/></System><RenderingInfo><EventName>SampleProfile</EventName></RenderingInfo></Event>
        </Events>
        """);
    var etw = EtwTraceAnalyzer.ParseXml(etwXml, root, "etw", executable, 42,
        DateTimeOffset.Parse("2026-10-26T10:00:00Z"),
        DateTimeOffset.Parse("2026-10-26T10:00:05Z"), CancellationToken.None);
    Check(etw.CpuSamples == 1 && etw.ContextSwitchesIn == 1 && etw.ReadyEvents == 1,
        "ETW parser did not isolate the target PID and its threads.");
    var timeline = await File.ReadAllTextAsync(Path.Combine(root, "traces", "normalized", "etw.timeline.jsonl"));
    var stacksJson = await File.ReadAllTextAsync(Path.Combine(root, "traces", "normalized", "etw.stacks.json"));
    Check(timeline.Contains("\"ready\"") && timeline.Contains("\"running\"")
        && timeline.Contains("\"offcpu\"") && stacksJson.Contains("\"samples\":1"),
        "ETW scheduler timeline/CPU stack evidence missing.");
    Check((await File.ReadAllLinesAsync(etw.Samples)).Length == 1
        && (await File.ReadAllTextAsync(etw.Samples)).Contains("0x1234")
        && (await File.ReadAllTextAsync(etw.Summary)).Contains("0x1234") == false,
        "ETW sample export or summary changed.");
    using (var summary = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(etw.Summary)))
    {
        Check(summary.RootElement.GetProperty("readyLatencySumMilliseconds").GetDouble() == 500
            && summary.RootElement.GetProperty("scheduledRunningMilliseconds").GetDouble() == 1000,
            "ETW ready/run intervals changed.");
    }
    var converter = new FakeTracerptProcess(etwXml);
    var converted = await new EtwTraceAnalyzer(converter).AnalyzeAsync("fixture.etl", root,
        "converted", executable, 42,
        DateTimeOffset.Parse("2026-10-26T10:00:00Z"),
        DateTimeOffset.Parse("2026-10-26T10:00:05Z"), CancellationToken.None);
    Check(converter.Arguments is not null
        && converter.Arguments[0] == "fixture.etl"
        && converter.Arguments.Contains("-lr")
        && converter.Arguments.Contains("XML")
        && converted.CpuSamples == 1,
        "tracerpt invocation or ETL conversion path changed.");

    var failedWpr = new FakeProcess { FailTarget = true };
    try
    {
        await new WindowsEtwBackend(failedWpr).CaptureAsync(request, CancellationToken.None);
        throw new Exception("Expected target failure");
    }
    catch (InvalidOperationException) { }
    Check(failedWpr.Calls.Last().Args.FirstOrDefault() == "-cancel", "WPR cleanup after target error");

    var analysis = Path.Combine(root, "analysis");
    Directory.CreateDirectory(Path.Combine(analysis, "traces", "raw"));
    await File.WriteAllTextAsync(Path.Combine(analysis, "traces", "raw", "counters.csv"),
        "100,,cycles\n250,,instructions\n5,,cache-misses\n2,,branch-misses\n");
    await File.WriteAllTextAsync(Path.Combine(analysis, "traces", "raw", "lto.csv"),
        "200,,cycles\n300,,instructions\n6,,cache-misses\n3,,branch-misses\n");
    await File.WriteAllTextAsync(Path.Combine(analysis, "counters_o3.results.json"),
        """{"captureId":"counters_o3","backend":"perf","mode":"counters","rawTrace":"traces/raw/counters.csv","eventSource":"Linux PMU counters"}""");
    await File.WriteAllTextAsync(Path.Combine(analysis, "counters_lto.results.json"),
        """{"captureId":"counters_lto","backend":"perf","mode":"counters","rawTrace":"traces/raw/lto.csv","eventSource":"Linux PMU counters"}""");
    await File.WriteAllTextAsync(Path.Combine(analysis, "run.json"), """
        {"nodes":[
          {"id":"build_o3","kind":"build","status":"succeeded"},
          {"id":"build_lto","kind":"build","status":"succeeded"},
          {"id":"timing_o3_1","kind":"run","status":"succeeded","processDurationSeconds":2.0},
          {"id":"timing_o3_2","kind":"run","status":"succeeded","processDurationSeconds":4.0},
          {"id":"timing_lto_1","kind":"run","status":"succeeded","processDurationSeconds":1.5},
          {"id":"timing_lto_2","kind":"run","status":"succeeded","processDurationSeconds":1.5}
        ]}
        """);
    await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None);
    Check(File.Exists(Path.Combine(analysis, "counter-comparison.json")),
        "perf stat counters were not analyzed separately.");
    using (var counters = System.Text.Json.JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(analysis, "counter-comparison.json"))))
    {
        Check(counters.RootElement.GetProperty("baseline").GetString() == "o3",
            "Counter comparison baseline followed directory enumeration order.");
        var variants = counters.RootElement.GetProperty("variants");
        Check(variants[0].GetProperty("derived").GetProperty("ipc").GetProperty("value").GetDouble() == 2.5
            && variants[0].GetProperty("derived").GetProperty("cache_mpki").GetProperty("value").GetDouble() == 20,
            "Derived perf metrics must use the same capture and correct denominators.");
        Check(variants[1].GetProperty("derived").GetProperty("ipc").GetProperty("ratioToBaseline").GetDouble() == 0.6,
            "Derived ratio baseline mismatch.");
    }
    using (var comparison = System.Text.Json.JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(analysis, "comparison.json"))))
    {
        var variants = comparison.RootElement.GetProperty("variants");
        Check(variants[0].GetProperty("medianSeconds").GetDouble() == 3.0
            && variants[1].GetProperty("ratioToBaseline").GetDouble() == 0.5,
            "Profile timing comparison changed.");
    }
    await File.WriteAllTextAsync(Path.Combine(analysis, "counters_o3.results.json"),
        """{"captureId":"counters_o3","backend":"perf","mode":"counters","rawTrace":"traces/raw/counters.csv","rawTraceSha256":"wrong","eventSource":"Linux PMU counters"}""");
    try
    {
        await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None);
        throw new Exception("Changed raw profile trace was accepted.");
    }
    catch (IOException) { }
    Console.WriteLine("Profiling backend checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeProcess : IProfileProcess
{
    public List<(string Executable, string[] Args)> Calls { get; } = [];
    public bool FailTarget { get; init; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var args = arguments.ToArray();
        Calls.Add((executable, args));
        if (FailTarget && executable != "wpr") throw new InvalidOperationException("target failed");
        if (executable == "perf")
        {
            var path = args[Array.IndexOf(args, "-o") + 1];
            File.WriteAllText(path, "perf fixture");
        }
        if (executable == "xcrun")
        {
            var path = args[Array.IndexOf(args, "--output") + 1];
            Directory.CreateDirectory(path);
        }
        if (executable == "wpr" && args[0] == "-stop") File.WriteAllText(args[1], "ETL fixture");
        return Task.FromResult(new CommandResult(0, "", false, executable == "wpr" ? null : 42));
    }
}

sealed class FakeTracerptProcess(string fixture) : IProfileProcess
{
    public string[]? Arguments { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        if (executable != "tracerpt") throw new Exception("Unexpected ETL converter.");
        Arguments = arguments.ToArray();
        File.Copy(fixture, arguments[Array.IndexOf(Arguments, "-o") + 1], overwrite: true);
        return Task.FromResult(new CommandResult(0, "", false));
    }
}
