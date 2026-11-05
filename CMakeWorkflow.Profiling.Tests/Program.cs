using CMakeWorkflow;
using CMakeWorkflow.Profiling;
using CMakeWorkflow.Profiling.Backends;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 1 && args[0] == "--progress-e2e")
{
    await ProgressE2E.RunAsync();
    return;
}

if (args.Length != 0)
{
    throw new ArgumentException("Usage: CMakeWorkflow.Profiling.Tests [--progress-e2e]");
}

string root = Path.Combine(Path.GetTempPath(), "cmake-workflow-profile-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Check(HistoricalProgress.TotalOperations(5, 3) == 397,
        "Historical progress step count changed.");
    // A real process executed through the shared workflow process owner must
    // be journalled before dispatch, replayable by identity and reject tampering.
    string dispatchRoot = Path.Combine(root, "dispatch-fixture");
    _ = Directory.CreateDirectory(Path.Combine(dispatchRoot, "logs"));
    JsonArray steps = [];
    List<WorkflowDispatchEvent> notifications = [];
    Dictionary<string, string> environment = [];
    WorkflowCommandDispatcher dispatcher = new(dispatchRoot, steps, false, environment,
        notifications.Add);
    const string dotnetExe = "dotnet";
    string actualVersion = await dispatcher.RunAsync("version", dotnetExe, ["--version"],
        null, 30, CancellationToken.None);
    Check(!string.IsNullOrWhiteSpace(actualVersion) && steps.Count == 1 &&
          notifications.Any(n => n.Type == "started") &&
          notifications.Any(n => n.Type == "succeeded"),
        "Shared command dispatch and progress events failed.");
    // Simulate a new process: deserialize committed journal bytes from disk.
    JsonArray committedSteps = JsonNode.Parse(
        await File.ReadAllTextAsync(Path.Combine(dispatchRoot, "commands.json")))!.AsArray();
    Check(committedSteps.Count == 1 && committedSteps[0]!["returncode"]!.GetValue<int>() == 0,
        "Shared command journal was not committed after external execution.");
    WorkflowCommandDispatcher resumed = new(dispatchRoot, committedSteps, true, environment,
        notifications.Add);
    string verifiedVersion = await resumed.RunAsync("version", dotnetExe, ["--version"],
        null, 30, CancellationToken.None);
    Check(verifiedVersion == actualVersion && committedSteps.Count == 1 &&
          notifications.Any(n => n.Type == "verified"),
        "Journal-based verification launched the command again.");
    await File.AppendAllTextAsync(Path.Combine(dispatchRoot, "logs", "version.stdout"), "tampered");
    bool rejectedChange = false;
    try
    {
        _ = await resumed.RunAsync("version", dotnetExe, ["--version"],
            null, 30, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        rejectedChange = true;
    }

    Check(rejectedChange, "Resume accepted a modified stdout artifact.");
    committedSteps.Add(new JsonObject { ["label"] = "unknown" });
    bool rejectedUnknown = false;
    try
    {
        _ = await resumed.RunAsync("unknown", dotnetExe, ["--version"],
            null, 30, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        rejectedUnknown = true;
    }

    Check(rejectedUnknown, "Resume replayed an unknown-effect operation.");

    string executable = Path.Combine(root, "test-worker");
    await File.WriteAllTextAsync(executable, "fixture");
    ProfileRequest request = new(executable, ["--workers", "1"], root,
        new Dictionary<string, string>(), root, "capture", "cpu", TimeSpan.FromSeconds(5));
    PythonShuffleRandom historicalOrder = new(20260930);
    List<int> points = [.. Enumerable.Range(0, 12)];
    historicalOrder.Shuffle(points);
    Check(points.SequenceEqual([8, 9, 1, 2, 5, 0, 11, 10, 4, 3, 6, 7]),
        "Historical paired shuffle schedule changed: " + string.Join(",", points));
    JsonObject timing = DagFlowHistoricalCapture.ParseTiming(
        "idle-burst p50= 42.5 p99= 99 max= 123\n", "idle-burst");
    Check(timing["time_us"]!.GetValue<double>() == 42.5
          && timing["p99_us"]!.GetValue<double>() == 99,
        "Historical timing units changed.");
    (JsonObject? Counts, JsonObject? Running) = DagFlowHistoricalCapture.ParseStat("""
                                                                100;;cycles:u;;100.00
                                                                200;;instructions:u;;100.00
                                                                300;;branches:u;;100.00
                                                                4;;branch-misses:u;;100.00
                                                                500;;cache-references:u;;100.00
                                                                6;;cache-misses:u;;100.00
                                                                7;;page-faults:u;;100.00
                                                                """);
    Check(Counts["cycles:u"]!.GetValue<double>() == 100
          && Running["page-faults"]!.GetValue<double>() == 100,
        "Historical perf counters or running percentages changed.");
    if (Environment.GetEnvironmentVariable("DAGFLOW_HISTORICAL_DATA_DIR") is { } data)
    {
        static JsonObject[] ReadRows(string path) =>
            [.. File.ReadLines(path).Where(x => x.Length > 0).Select(x => JsonNode.Parse(x)!.AsObject())];

        JsonArray summarized = DagFlowHistoricalCapture.Summarize(
            ReadRows(Path.Combine(data, "timings.jsonl")),
            ReadRows(Path.Combine(data, "counters.jsonl")));
        JsonArray expected = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(data, "summary.json"), CancellationToken.None))!.AsArray();
        Check(summarized.Count == expected.Count, "Historical summary row count changed.");
        for (int i = 0; i < summarized.Count; i++)
        {
            foreach ((string? key, JsonNode? value) in expected[i]!.AsObject())
            {
                JsonNode? actual = summarized[i]![key];
                if (value is null || actual is null)
                {
                    continue;
                }

                if (value is JsonValue && actual is JsonValue
                                                              && double.TryParse(value.ToString(), NumberStyles.Float,
                                                                  CultureInfo.InvariantCulture, out double numeric)
                                                              && double.TryParse(actual.ToString(), NumberStyles.Float,
                                                                  CultureInfo.InvariantCulture, out double computed))
                {
                    Check(Math.Abs(numeric - computed) < 1e-7 * Math.Max(1, Math.Abs(numeric)),
                        "Historical metric changed: " + i + "/" + key);
                }
                else
                {
                    Check(value.ToString() == actual.ToString(),
                        "Historical summary value changed: " + i + "/" + key);
                }
            }
        }
    }

    using (StringReader weighted = new("""
                                           worker 100/101 [001] 123.456: 17 cycles:u:
                                                        7ff0 Work::execute (dagflow-runtime-bench)
                                                        7ff1 Pool::worker (dagflow-runtime-bench)
                                           worker 100/101 [001] 123.457: 23 cycles:u:
                                                        7ff0 Work::execute (dagflow-runtime-bench)
                                                        7ff1 Pool::worker (dagflow-runtime-bench)
                                           """))
    {
        JsonObject tree = DagFlowHistoricalFlamegraph.Parse(weighted, CancellationToken.None);
        Check(tree["metadata"]!["samples"]!.GetValue<long>() == 2
              && tree["format"]!.GetValue<string>() == "flat-prefix-tree-v1"
              && DagFlowHistoricalFlamegraph.ReadFrames(tree)[^1].Self == 40
              && DagFlowHistoricalFlamegraph.Svg(tree, "weighted test").Contains("sampled cycles", StringComparison.Ordinal),
            "Period-weighted flamegraph conversion failed.");
        // Thousands of real stack frames must neither overflow JSON's nesting
        // limit nor recurse through frame trees when producing SVG/statistics.
        StringBuilder deepPerf = new("deep 100/101 [001] 123.456: 37 cycles:u:\n");
        for (int frameIndex = 0; frameIndex < 1500; frameIndex++)
        {
            _ = deepPerf.Append("    7ff0 VeryDeep::frame_")
                .Append(frameIndex.ToString(CultureInfo.InvariantCulture))
                .Append(" (dagflow-runtime-bench)\n");
        }

        JsonObject deepTree = DagFlowHistoricalFlamegraph.Parse(
            new StringReader(deepPerf.ToString()), CancellationToken.None);
        IReadOnlyList<DagFlowHistoricalFlamegraph.WeightedFrame> deepFrames = DagFlowHistoricalFlamegraph.ReadFrames(deepTree);
        Check(deepFrames.Count == 1502 && deepFrames[^1].Self == 37
                                       && deepTree.ToJsonString().Length > 0
                                       && DagFlowHistoricalFlamegraph.Svg(deepTree, "deep test")
                                           .Contains("VeryDeep::frame_1499", StringComparison.Ordinal),
            "Deep flamegraph lost weighted frames or exceeded JSON nesting depth.");
        // Reports written before this change use nested frame trees.
        JsonObject legacy = JsonNode.Parse(
                """{"n":"all","v":0,"c":[{"n":"worker","v":17,"c":[]}],"metadata":{"samples":1,"missing_callchain_samples":0}}""")
            !.AsObject();
        Check(DagFlowHistoricalFlamegraph.ReadFrames(legacy).Count == 2
              && DagFlowHistoricalFlamegraph.Svg(legacy, "legacy test").Contains("worker", StringComparison.Ordinal),
            "Legacy nested flamegraphs are no longer renderable.");
        string historical = Path.Combine(root, "historical");
        string snapshot = Path.Combine(historical, "source");
        string profile = Path.Combine(historical, "profiles", "o3-1-idle-burst");
        _ = Directory.CreateDirectory(snapshot);
        _ = Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(snapshot, "source.cpp"), "source");
        await File.WriteAllTextAsync(Path.Combine(profile, "flamegraph.json"), tree.ToJsonString());
        string sourceHash =
            await CommandExecution.Sha256Async(Path.Combine(snapshot, "source.cpp"), CancellationToken.None);
        string binaryHash = await CommandExecution.Sha256Async(executable, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(historical, "manifest.json"),
            new JsonObject
            {
                ["source_sha256"] = new JsonObject { ["source.cpp"] = sourceHash },
                ["binaries"] = new JsonObject
                {
                    ["o3"] = new JsonObject
                    {
                        ["path"] = executable,
                        ["sha256"] = binaryHash
                    }
                }
            }.ToJsonString());
        JsonArray historicalRows = [];
        foreach (string? profileName in new[] { "o3", "o3-lto" })
        {
            foreach (int workers in new[] { 1, 4 })
            {
                foreach (string? scenario in new[]
                 {
                     "external-contention", "hot-shard-skew",
                     "local-overflow", "nested-helping", "mixed-chaos", "idle-burst"
                 })
                {
                    string fixtureProfile = Path.Combine(historical, "profiles",
                $"{profileName}-{workers}-{scenario}");
                    _ = Directory.CreateDirectory(fixtureProfile);
                    await File.WriteAllTextAsync(Path.Combine(fixtureProfile, "flamegraph.json"),
                        tree.ToJsonString());
                    historicalRows.Add(new JsonObject
                    {
                        ["profile"] = profileName,
                        ["workers"] = workers,
                        ["scenario"] = scenario,
                        ["time_us"] = 42,
                        ["cycles:u"] = 40000000,
                        ["instructions:u"] = 10000000,
                        ["branches:u"] = 2000000,
                        ["branch-misses:u"] = 100,
                        ["cache-references:u"] = 1000,
                        ["cache-misses:u"] = 100,
                        ["page-faults"] = 7
                    });
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(historical, "summary.json"),
            historicalRows.ToJsonString());
        _ = await DagFlowHistoricalReport.RenderAsync(historical, CancellationToken.None);
        Check((await File.ReadAllTextAsync(Path.Combine(profile, "flamegraph.svg"), CancellationToken.None)).Contains("Work::execute", StringComparison.Ordinal)
              && (await File.ReadAllTextAsync(Path.Combine(profile, "flamegraph.svg"), CancellationToken.None)).Contains("Search frames", StringComparison.Ordinal)
              && (await File.ReadAllTextAsync(Path.Combine(historical, "index.html"), CancellationToken.None)).Contains("idle-burst", StringComparison.Ordinal)
              && File.Exists(Path.Combine(historical, "README.md")),
            "Historical profile rendering dropped symbols, search UX, or summary rows.");
        // The final report must handle both a very deep newly-generated tree
        // and a previously persisted nested flamegraph in the same campaign.
        await File.WriteAllTextAsync(Path.Combine(profile, "flamegraph.json"),
            deepTree.ToJsonString());
        string legacyPath = Path.Combine(historical, "profiles", "o3-1-local-overflow", "flamegraph.json");
        await File.WriteAllTextAsync(legacyPath, legacy.ToJsonString());
        _ = await DagFlowHistoricalReport.RenderAsync(historical, CancellationToken.None);
        JsonNode deepStatistics = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(historical, "sampling-summary.json")))!
            .AsObject()["o3-1-idle-burst"]!;
        Check(deepStatistics["max_depth"]!.GetValue<int>() == 1501
              && deepStatistics["sampled_cycles"]!.GetValue<long>() == 37,
            "Historical report cannot summarize a deep flamegraph.");
        JsonNode lastRow = historicalRows[historicalRows.Count - 1]!.DeepClone();
        historicalRows.RemoveAt(historicalRows.Count - 1);
        await File.WriteAllTextAsync(Path.Combine(historical, "summary.json"), historicalRows.ToJsonString());
        try
        {
            _ = await DagFlowHistoricalReport.RenderAsync(historical, CancellationToken.None);
            throw new System.Diagnostics.UnreachableException("Historical renderer accepted a truncated campaign.");
        }
        catch (InvalidDataException)
        {
        }

        historicalRows.Add(lastRow);
        await File.WriteAllTextAsync(Path.Combine(historical, "summary.json"), historicalRows.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(snapshot, "source.cpp"), "mutated");
        try
        {
            _ = await DagFlowHistoricalReport.RenderAsync(historical, CancellationToken.None);
            throw new System.Diagnostics.UnreachableException("Historical renderer accepted a changed source snapshot.");
        }
        catch (InvalidDataException)
        {
        }
    }

    using (StringReader script = new("""
                                         test 100 [001] 123.456: 1 cycles:u:
                                                      7ff0 Work::execute (dagflow-runtime-bench)
                                                      7ff1 Pool::worker (dagflow-runtime-bench)
                                         test 100 [001] 123.457: 1 cycles:u:
                                                      7ff0 Work::execute (dagflow-runtime-bench)
                                                      7ff1 Pool::worker (dagflow-runtime-bench)
                                         """))
    {
        JsonObject stacks = PerfStackAnalyzer.Parse(script, CancellationToken.None).ToJson("perf", "capture");
        Check(stacks["samples"]!.GetValue<int>() == 2
              && stacks["symbolizedSamples"]!.GetValue<int>() == 2
              && stacks["stacks"]!.AsArray()[0]!["count"]!.GetValue<int>() == 2,
            "perf script folded stack parsing or normalization failed.");
    }

    using (StringReader script = new("""
                                         dagflow-public- 1804384/1804384 835331.745025: 1 cycles:u:
                                                     7f10 dagflow::TaskGraph::seal() (/tmp/dagflow_public_api_bench)
                                                     7f11 dagflow::Pool::worker_loop() (/tmp/libdagflow.so)

                                         worker 101/102 835332.000001: 1 cpu-clock:u:
                                                     0000000000400010 [unknown] (/tmp/dagflow_public_api_bench)
                                                     7f11 dagflow::Pool::worker_loop() (/tmp/libdagflow.so)

                                         worker 101 [003] 835333.000001: 1 cycles:
                                                     7f20 dagflow::TaskGraph::add_edge() (/tmp/dagflow_public_api_bench)

                                         nonsense unsupported header
                                         """))
    {
        StackEvidence parsed = PerfStackAnalyzer.Parse(script, CancellationToken.None);
        JsonObject stacks = parsed.ToJson("perf", "capture");
        Check(stacks["samples"]!.GetValue<int>() == 3
              && stacks["analysisStatus"]!.GetValue<string>() == "partial"
              && stacks["unknownLeafFrames"]!.GetValue<int>() == 1
              && stacks["resolvedFrames"]!.GetValue<int>() == 4,
            "Real perf headers, mixed records, and frame quality were not parsed.");
        JsonNode first = stacks["stacks"]!.AsArray().First(x =>
            x!["frames"]!.AsArray().Any(f => f!.GetValue<string>().Contains("TaskGraph::seal", StringComparison.Ordinal)))!;
        Check(first["frames"]!.AsArray()[0]!.GetValue<string>().Contains("worker_loop", StringComparison.Ordinal)
              && first["frames"]!.AsArray()[0]!.GetValue<string>().Contains("libdagflow.so", StringComparison.Ordinal)
              && first["frames"]!.AsArray()[1]!.GetValue<string>().Contains("TaskGraph::seal", StringComparison.Ordinal),
            "Call chain order or DSO attribution changed.");
    }

    // Some perf script -F formats place the sampled IP/symbol/DSO on the
    // event header itself, followed by caller frames. The sampled leaf must
    // remain in the stack and in the self-sample accounting.
    using (StringReader script = new("""
                                         dagflow 123/124 [001] 456.123456: 42 cycles:u: 7f123 dagflow::Pool::worker_loop() (/tmp/libdagflow.so)
                                                     7f456 std::thread::_M_run() (/usr/lib/libstdc++.so)

                                         dagflow 123/124 [001] 456.123457: 42 cycles:u: 7f999 dagflow::TaskGraph::seal() (/tmp/libdagflow.so)

                                         """))
    {
        JsonObject stacks = PerfStackAnalyzer.Parse(script, CancellationToken.None).ToJson("perf", "capture");
        Check(stacks["samples"]!.GetValue<int>() == 2
              && stacks["parsedSamples"]!.GetValue<int>() == 2
              && stacks["analysisStatus"]!.GetValue<string>() == "complete"
              && stacks["resolvedFrames"]!.GetValue<int>() == 3,
            "Inline perf script sample symbols were discarded or double-counted.");
        JsonArray hot = stacks["hotFunctions"]!.AsArray();
        Check(hot.Any(x => x!["frame"]!.GetValue<string>().Contains("Pool::worker_loop", StringComparison.Ordinal))
              && hot.Any(x => x!["frame"]!.GetValue<string>().Contains("TaskGraph::seal", StringComparison.Ordinal)),
            "Inline sampled leaf was not counted as a hot function.");
        JsonNode twoFrame = stacks["stacks"]!.AsArray().Single(x => x!["frames"]!.AsArray().Count == 2)!;
        Check(twoFrame["frames"]!.AsArray()[0]!.GetValue<string>().Contains("std::thread", StringComparison.Ordinal)
              && twoFrame["frames"]!.AsArray()[1]!.GetValue<string>().Contains("Pool::worker_loop", StringComparison.Ordinal),
            "Inline leaf and caller order is not root-to-leaf.");
    }

    using (StringReader script = new("""
                                         dagflow 123/124 456.123456: 42 cycles:u:

                                         dagflow 123/124 456.123457: 42 cycles:u:
                                                     7f123 dagflow::Pool::worker_loop() (/tmp/libdagflow.so)
                                         """))
    {
        JsonObject stacks = PerfStackAnalyzer.Parse(script, CancellationToken.None).ToJson("perf", "capture");
        Check(stacks["analysisStatus"]!.GetValue<string>() == "partial"
              && stacks["samples"]!.GetValue<int>() == 2
              && stacks["parsedSamples"]!.GetValue<int>() == 1,
            "Empty sampled call chains must not be claimed as complete.");
    }

    using (StringReader script = new(""))
    {
        Check(PerfStackAnalyzer.Parse(script, CancellationToken.None).AnalysisStatus == "no_samples",
            "Empty trace status changed.");
    }

    using (StringReader script = new("unsupported header\n"))
    {
        Check(PerfStackAnalyzer.Parse(script, CancellationToken.None).AnalysisStatus == "unsupported_format",
            "Unsupported nonempty trace was interpreted as empty.");
    }

    using (StringReader script = new("""
                                         cmd 123 [003] 123.001234: sched:sched_wakeup: comm=worker pid=42 prio=120 target_cpu=003
                                         cmd 123 [003] 123.002345: sched:sched_switch: prev_comm=worker prev_pid=42 prev_prio=120 prev_state=S ==> next_comm=other next_pid=43 next_prio=120
                                         """))
    using (StringWriter output = new())
    {
        Check(PerfSchedulerAnalyzer.Parse(script, output, CancellationToken.None) == 3
              && output.ToString().Contains("\"ready\"", StringComparison.Ordinal)
              && output.ToString().Contains("\"offcpu\"", StringComparison.Ordinal)
              && output.ToString().Contains("\"running\"", StringComparison.Ordinal)
              && output.ToString().Contains("perf-relative", StringComparison.Ordinal),
            "perf scheduler tracepoint normalization failed.");
    }

    FakeProcess perf = new();
    ProfileResult perfResult = await new LinuxPerfBackend(perf).CaptureAsync(request, CancellationToken.None);
    Check(perf.Calls.Count == 1 && perf.Calls[0].Executable == "perf"
                                && perf.Calls[0].Args.Take(2).SequenceEqual(["record", "-o"])
                                && perf.Calls[0].Args.Contains("--call-graph")
                                && perf.Calls[0].Args.Contains("--user-callchains")
                                && perf.Calls[0].Args.Contains("dwarf,16384")
                                && perf.Calls[0].Args.TakeLast(3).SequenceEqual([executable, "--workers", "1"])
                                && File.Exists(perfResult.RawTrace), "perf capture invocation or artifact");
    ProfileResult counterResult = await new LinuxPerfBackend(perf).CaptureAsync(
        request with { CaptureId = "counters", Mode = "counters" }, CancellationToken.None);
    Check(perf.Calls[1].Args.Take(3).SequenceEqual(["stat", "-x", ","])
          && counterResult.RawTrace.EndsWith(".csv", StringComparison.Ordinal)
          && File.Exists(counterResult.RawTrace), "perf counter capture must remain separate from sampling");
    LinuxPerfBackend pinned = new(perf);
    _ = await pinned.CaptureAsync(request with
    {
        CaptureId = "pinned",
        CpuSet = "2",
        CpuAffinityLauncher = "/usr/bin/taskset"
    }, CancellationToken.None);
    Check(perf.Calls[2].Args.TakeLast(6).SequenceEqual(
            ["/usr/bin/taskset", "-c", "2", executable, "--workers", "1"]),
        "Required affinity was not inserted between profiler and target exec.");
    try
    {
        // Select the native backend on each supported host. The test is for
        // required capability admission, not platform rejection of Linux perf.
        string hostBackend = OperatingSystem.IsLinux() ? "perf" :
            OperatingSystem.IsWindows() ? "etw" : "instruments";
        _ = await new ProfileWorkflowExecutor(perf).ExecuteAsync(new WorkflowOperationRequest(
            "profile", executable, [], root, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["backend"] = "auto", ["mode"] = "cpu" },
            root, "mismatched-capability", RequiredProfiler: new WorkflowProfilerRequirement
            { Backend = hostBackend, Mode = "cpu", EventSource = "unavailable" }), CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Unsupported required profiler capability was accepted.");
    }
    catch (InvalidOperationException error) when (error.Message.Contains("Required profiler capabilities", StringComparison.Ordinal))
    {
    }

    Check(perf.Calls.Count == 3 && !File.Exists(Path.Combine(root, "mismatched-capability.manifest.json")),
        "Profiler was started before required capabilities were checked.");

    // No profiler tool is invoked by replay when a stored executable is
    // stale or both variants point to the same mutable build output.
    string replayExe = Path.Combine(root, "replay-executable");
    await File.WriteAllTextAsync(replayExe, "immutable-benchmark");
    string replayHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        await File.ReadAllBytesAsync(replayExe)));
    string replayDir = Path.Combine(root, "replay-fixture");
    _ = Directory.CreateDirectory(replayDir);
    JsonObject report = new()
    {
        ["status"] = "succeeded",
        ["nodes"] = new JsonArray(new JsonObject
        {
            ["id"] = "sample",
            ["kind"] = "run",
            ["status"] = "succeeded",
            ["artifact"] = new JsonObject
            {
                ["executable"] = replayExe,
                ["sha256"] = replayHash
            },
            ["environmentOverrideHash"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["arguments"] = new JsonArray(
                JsonValue.Create("--scenario"),
                JsonValue.Create("chain")),
            ["workingDirectory"] = root
        })
    };
    await File.WriteAllTextAsync(Path.Combine(replayDir, "run.json"), report.ToJsonString());
    try
    {
        _ = await ProfileReplayCommand.ExecuteAsync(["replay", replayDir, replayDir, "--node", "sample"],
            CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Expected same-binary replay rejection");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("same mutable executable", StringComparison.Ordinal))
    {
    }

    await File.WriteAllTextAsync(replayExe, "changed-binary");
    try
    {
        _ = await ProfileReplayCommand.ExecuteAsync(["replay", replayDir, replayDir, "--node", "sample"],
            CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Expected stale SHA rejection");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("changed since its original run",
                                                  StringComparison.Ordinal))
    {
    }

    // Native harness case replay must use snapshotted executable bytes, not
    // mutable CMake target paths; unsupported/ambiguous provenance fails closed.
    string nativeA = Path.Combine(root, "harness-before");
    string nativeB = Path.Combine(root, "harness-after");
    foreach (string? dir in new[] { nativeA, nativeB })
    {
        string artifacts = Path.Combine(dir, "harness", "api-workloads");
        _ = Directory.CreateDirectory(artifacts);
        string preserved = Path.Combine(artifacts, OperatingSystem.IsWindows() ? "replay-binary.exe" : "replay-binary");
        await File.WriteAllTextAsync(preserved, dir);
        string binarySha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(preserved)));
        JsonObject manifest = new()
        {
            ["binary"] = Path.Combine(dir, "mutable-old-binary"),
            ["binary_sha256"] = binarySha,
            ["cases"] = new JsonObject
            {
                ["chain-4"] = new JsonObject
                {
                    ["args"] = new JsonArray(
                        JsonValue.Create("--scenario"),
                        JsonValue.Create("chain")),
                    ["metadata"] = new JsonObject()
                }
            },
            ["replayCases"] = new JsonObject
            {
                ["chain-4"] = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["replayable"] = true,
                    ["arguments"] = new JsonArray(
                        JsonValue.Create("--scenario"),
                        JsonValue.Create("chain")),
                    ["workingDirectory"] = root,
                    ["cpuSet"] = ""
                }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "manifest.json"), manifest.ToJsonString());
        string fileName = Path.GetFileName(preserved);
        JsonObject reportHarness = new()
        {
            ["status"] = "succeeded",
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["id"] = "api-workloads",
                ["kind"] = "invoke",
                ["status"] = "succeeded",
                ["outputs"] = new JsonObject
                {
                    ["dagflow-manifest.json"] = "harness/api-workloads/manifest.json",
                    ["dagflow-replay-binary"] = "harness/api-workloads/" + fileName
                }
            })
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "run.json"), reportHarness.ToJsonString());
    }

    string[] harnessArgs = ["replay", nativeA, nativeB, "--node", "api-workloads", "--case", "chain-4"];
    // These rejection tests execute before any profiler is launched.
    try
    {
        _ = await ProfileReplayCommand.ExecuteAsync(
            ["replay", nativeA, nativeB, "--node", "api-workloads", "--case", "missing"], CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Expected missing native case rejection");
    }
    catch (InvalidDataException e) when (e.Message.Contains("no native replay descriptor", StringComparison.Ordinal))
    {
    }

    string tampered = Path.Combine(nativeA, "harness", "api-workloads",
        OperatingSystem.IsWindows() ? "replay-binary.exe" : "replay-binary");
    await File.AppendAllTextAsync(tampered, "tamper");
    try
    {
        _ = await ProfileReplayCommand.ExecuteAsync(harnessArgs, CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Expected preserved-binary SHA rejection");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("Snapshotted benchmark binary changed",
                                                  StringComparison.Ordinal))
    {
    }

    // Keep baseline bytes valid, but reject a non-replayable case explicitly.
    await File.WriteAllTextAsync(tampered, nativeA);
    string candidateManifest = Path.Combine(nativeB, "harness", "api-workloads", "manifest.json");
    JsonObject candidateObject =
        JsonNode.Parse(await File.ReadAllTextAsync(candidateManifest))!.AsObject();
    candidateObject["replayCases"]!["chain-4"]!["replayable"] = false;
    candidateObject["replayCases"]!["chain-4"]!["reason"] = "environment overrides";
    await File.WriteAllTextAsync(candidateManifest, candidateObject.ToJsonString());
    try
    {
        _ = await ProfileReplayCommand.ExecuteAsync(harnessArgs, CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Expected unsupported native harness case rejection");
    }
    catch (InvalidDataException e) when (e.Message.Contains("environment overrides", StringComparison.Ordinal))
    {
    }

    FakeProcess instruments = new();
    ProfileResult macResult = await new MacInstrumentsBackend(instruments).CaptureAsync(request, CancellationToken.None);
    Check(instruments.Calls.Count == 1 && instruments.Calls[0].Executable == "xcrun"
                                       && instruments.Calls[0].Args.Take(2).SequenceEqual(["xctrace", "record"])
                                       && instruments.Calls[0].Args.Contains("--launch")
                                       && Directory.Exists(macResult.RawTrace),
        "xctrace capture invocation or artifact");
    string emptyTraceHash = await ProfileArtifactHash.Sha256Async(macResult.RawTrace, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(macResult.RawTrace, "trace.db"), "fixture");
    Check(emptyTraceHash != await ProfileArtifactHash.Sha256Async(macResult.RawTrace, CancellationToken.None),
        "Instruments directory artifact hash ignored contents.");

    FakeProcess wpr = new();
    ProfileResult etwResult = await new WindowsEtwBackend(wpr).CaptureAsync(request, CancellationToken.None);
    Check(wpr.Calls.Select(x => x.Executable).SequenceEqual(["wpr", "wpr", executable, "wpr"]),
        "WPR must start before launching the target and stop afterward");
    Check(wpr.Calls[1].Args.Take(3).SequenceEqual(["-start", "CPU.light", "-filemode"])
          && wpr.Calls[3].Args[0] == "-stop"
          && wpr.Calls.Where(x => x.Executable == "wpr").All(x => x.Args[^2] == "-instancename")
          && File.Exists(etwResult.RawTrace) && etwResult.TargetProcessId == 42
          && etwResult.TargetStartedAt is not null && etwResult.TargetFinishedAt is not null,
        "WPR named session, target identity or ETL artifact");

    string etwXml = Path.Combine(root, "events.xml");
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
    EtwAnalysisResult etw = EtwTraceAnalyzer.ParseXml(etwXml, root, "etw", executable, 42,
        DateTimeOffset.Parse("2026-10-26T10:00:00Z", CultureInfo.CurrentCulture),
        DateTimeOffset.Parse("2026-10-26T10:00:05Z", CultureInfo.CurrentCulture), CancellationToken.None);
    Check(etw.CpuSamples == 1 && etw.ContextSwitchesIn == 1 && etw.ReadyEvents == 1,
        "ETW parser did not isolate the target PID and its threads.");
    string timeline = await File.ReadAllTextAsync(Path.Combine(root, "traces", "normalized", "etw.timeline.jsonl"));
    string stacksJson = await File.ReadAllTextAsync(Path.Combine(root, "traces", "normalized", "etw.stacks.json"));
    Check(timeline.Contains("\"ready\"", StringComparison.Ordinal) && timeline.Contains("\"running\"", StringComparison.Ordinal)
                                         && timeline.Contains("\"offcpu\"", StringComparison.Ordinal) && stacksJson.Contains("\"samples\":1", StringComparison.Ordinal),
        "ETW scheduler timeline/CPU stack evidence missing.");
    Check((await File.ReadAllLinesAsync(etw.Samples)).Length == 1
          && (await File.ReadAllTextAsync(etw.Samples)).Contains("0x1234", StringComparison.Ordinal)
          && !(await File.ReadAllTextAsync(etw.Summary)).Contains("0x1234", StringComparison.Ordinal),
        "ETW sample export or summary changed.");
    using (JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(etw.Summary)))
    {
        Check(summary.RootElement.GetProperty("readyLatencySumMilliseconds").GetDouble() == 500
              && summary.RootElement.GetProperty("scheduledRunningMilliseconds").GetDouble() == 1000,
            "ETW ready/run intervals changed.");
    }

    FakeTracerptProcess converter = new(etwXml);
    EtwAnalysisResult converted = await new EtwTraceAnalyzer(converter).AnalyzeAsync("fixture.etl", root,
        "converted", executable, 42,
        DateTimeOffset.Parse("2026-10-26T10:00:00Z", CultureInfo.CurrentCulture),
        DateTimeOffset.Parse("2026-10-26T10:00:05Z", CultureInfo.CurrentCulture), CancellationToken.None);
    Check(converter.Arguments is not null
          && converter.Arguments[0] == "fixture.etl"
          && converter.Arguments.Contains("-lr")
          && converter.Arguments.Contains("XML")
          && converted.CpuSamples == 1,
        "tracerpt invocation or ETL conversion path changed.");

    FakeProcess failedWpr = new() { FailTarget = true };
    try
    {
        _ = await new WindowsEtwBackend(failedWpr).CaptureAsync(request, CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Expected target failure");
    }
    catch (InvalidOperationException)
    {
    }

    Check(failedWpr.Calls.Last().Args.FirstOrDefault() == "-cancel", "WPR cleanup after target error");

    string analysis = Path.Combine(root, "analysis");
    _ = Directory.CreateDirectory(Path.Combine(analysis, "traces", "raw"));
    await File.WriteAllTextAsync(Path.Combine(analysis, "traces", "raw", "counters.csv"),
        "100,,cycles\n250,,instructions\n5,,cache-misses\n2,,branch-misses\n");
    await File.WriteAllTextAsync(Path.Combine(analysis, "traces", "raw", "lto.csv"),
        "200,,cycles\n300,,instructions\n6,,cache-misses\n3,,branch-misses\n");
    await File.WriteAllTextAsync(Path.Combine(analysis, "counters_o3.results.json"),
                             /*lang=json,strict*/
                             """{"captureId":"counters_o3","backend":"perf","mode":"counters","rawTrace":"traces/raw/counters.csv","eventSource":"Linux PMU counters"}""");
    await File.WriteAllTextAsync(Path.Combine(analysis, "counters_lto.results.json"),
                             /*lang=json,strict*/
                             """{"captureId":"counters_lto","backend":"perf","mode":"counters","rawTrace":"traces/raw/lto.csv","eventSource":"Linux PMU counters"}""");
    await File.WriteAllTextAsync(Path.Combine(analysis, "run.json"), /*lang=json,strict*/ """
                                                                     {"nodes":[
                                                                       {"id":"build_o3","kind":"build","status":"succeeded"},
                                                                       {"id":"build_lto","kind":"build","status":"succeeded"},
                                                                       {"id":"timing_o3_1","kind":"run","status":"succeeded","processDurationSeconds":2.0},
                                                                       {"id":"timing_o3_2","kind":"run","status":"succeeded","processDurationSeconds":4.0},
                                                                       {"id":"timing_lto_1","kind":"run","status":"succeeded","processDurationSeconds":1.5},
                                                                       {"id":"timing_lto_2","kind":"run","status":"succeeded","processDurationSeconds":1.5}
                                                                     ]}
                                                                     """);
    _ = await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None);
    Check(File.Exists(Path.Combine(analysis, "counter-comparison.json")),
        "perf stat counters were not analyzed separately.");
    using (JsonDocument counters = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "counter-comparison.json"))))
    {
        Check(counters.RootElement.GetProperty("baseline").GetString() == "o3",
            "Counter comparison baseline followed directory enumeration order.");
        JsonElement variants = counters.RootElement.GetProperty("variants");
        Check(variants[0].GetProperty("derived").GetProperty("ipc").GetProperty("value").GetDouble() == 2.5
              && variants[0].GetProperty("derived").GetProperty("cache_mpki").GetProperty("value").GetDouble() == 20,
            "Derived perf metrics must use the same capture and correct denominators.");
        Check(variants[1].GetProperty("derived").GetProperty("ipc").GetProperty("ratioToBaseline").ValueKind
              == JsonValueKind.Null,
            "Unknown running fractions must not produce a counter comparison ratio.");
    }

    using (JsonDocument comparison = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "comparison.json"))))
    {
        JsonElement variants = comparison.RootElement.GetProperty("variants");
        Check(variants[0].GetProperty("medianSeconds").GetDouble() == 3.0
              && variants[1].GetProperty("ratioToBaseline").GetDouble() == 0.5,
            "Profile timing comparison changed.");
    }

    string modifiedCounters = Path.Combine(analysis, "traces", "raw", "counters.csv");
    await File.WriteAllTextAsync(modifiedCounters,
        "5899297,,cycles:u,4900589,100.00,,\n7634603,,instructions:u,4900589,100.00,,\n" +
        "71172,,cache-misses:u,4900589,100.00,,\n22259,,branch-misses:u,4900589,100.00,,\n");
    _ = await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None);
    using (JsonDocument counters = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "counter-comparison.json"))))
    {
        JsonElement derived = counters.RootElement.GetProperty("variants")[0].GetProperty("derived");
        Check(Math.Abs(derived.GetProperty("ipc").GetProperty("value").GetDouble() - 1.294) < .001
              && Math.Abs(derived.GetProperty("cache_mpki").GetProperty("value").GetDouble() - 9.32) < .01
              && Math.Abs(derived.GetProperty("branch_mpki").GetProperty("value").GetDouble() - 2.92) < .01
              && derived.GetProperty("ipc").GetProperty("domain").GetString() == "user",
            "Modified perf events did not yield compatible IPC and MPKI.");
    }

    await File.WriteAllTextAsync(modifiedCounters,
        "100,,cycles:u,100,50.00,,\n200,,instructions:k,100,50.00,,\n" +
        "<not supported>,,cache-misses:u,0,0.00,,\n");
    _ = await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None);
    using (JsonDocument counters = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "counter-comparison.json"))))
    {
        Check(!counters.RootElement.GetProperty("variants")[0].GetProperty("derived").TryGetProperty("ipc", out _),
            "Mixed user/kernel counter domains produced misleading IPC.");
    }

    using (JsonDocument analyzed = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "analysis.json"))))
    {
        Check(analyzed.RootElement.EnumerateArray().Any(x =>
                x.GetProperty("captureId").GetString() == "counters_o3"
                && x.GetProperty("analysisStatus").GetString() == "partial"
                && x.GetProperty("analysisWarnings").GetArrayLength() > 0),
            "Unsupported perf stat rows or incompatible domains were silently discarded.");
    }

    string failedCapture = Path.Combine(analysis, "capture_failed.results.json");
    await File.WriteAllTextAsync(failedCapture,
                             /*lang=json,strict*/
                             """{"captureId":"capture_failed","backend":"perf","mode":"cpu","status":"failed"}""");
    Check(await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None) == 1,
        "Failed capture was reported as successful analysis.");
    using (JsonDocument analyzed = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "analysis.json"))))
    {
        Check(analyzed.RootElement.EnumerateArray().Any(x =>
                x.GetProperty("captureId").GetString() == "capture_failed"
                && x.GetProperty("analysisStatus").GetString() == "capture_failed"),
            "Failed capture status was not preserved.");
    }

    File.Delete(failedCapture);
    // A failed ProfileSession persists its manifest and journal entry, but
    // deliberately does not commit a *.results.json capture record.
    await File.WriteAllTextAsync(Path.Combine(analysis, "capture_interrupted.manifest.json"),
                             /*lang=json,strict*/
                             """{"captureId":"capture_interrupted","backend":"perf","mode":"cpu"}""");
    await File.WriteAllTextAsync(Path.Combine(analysis, "runs.jsonl"),
        /*lang=json,strict*/
        "{\"captureId\":\"capture_interrupted\",\"status\":\"failed\",\"error\":\"perf record denied\"}\n");
    Check(await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None) == 1,
        "Manifest without capture results was reported as a successful analysis.");
    using (JsonDocument analyzed = JsonDocument.Parse(
               await File.ReadAllTextAsync(Path.Combine(analysis, "analysis.json"))))
    {
        Check(analyzed.RootElement.EnumerateArray().Any(x =>
                x.GetProperty("captureId").GetString() == "capture_interrupted"
                && x.GetProperty("captureStatus").GetString() == "failed"
                && x.GetProperty("analysisStatus").GetString() == "capture_incomplete"),
            "Interrupted capture lost its failure evidence.");
    }

    File.Delete(Path.Combine(analysis, "capture_interrupted.manifest.json"));
    File.Delete(Path.Combine(analysis, "runs.jsonl"));
    await File.WriteAllTextAsync(Path.Combine(analysis, "counters_o3.results.json"),
                             /*lang=json,strict*/
                             """{"captureId":"counters_o3","backend":"perf","mode":"counters","rawTrace":"traces/raw/counters.csv","rawTraceSha256":"wrong","eventSource":"Linux PMU counters"}""");
    try
    {
        _ = await ProfileAnalyzer.AnalyzeAsync(analysis, CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Changed raw profile trace was accepted.");
    }
    catch (IOException)
    {
    }

    if (OperatingSystem.IsLinux())
    {
        await HistoricalFixture.RunAsync(root);
    }

    Console.WriteLine("Profiling backend checks passed.");
}

finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new System.Diagnostics.UnreachableException(message);
    }
}

internal static class HistoricalFixture
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static async Task RunAsync(string root)
    {
        string source = Path.Combine(root, "historical-fixture-source");
        foreach (string? dir in new[] { "include", "src", "cmake", "bench" })
        {
            _ = Directory.CreateDirectory(Path.Combine(source, dir));
        }

        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(source, "bench", "stress_harness.cpp"), "fixture");
        string tools = Path.Combine(root, "fake-tools");
        _ = Directory.CreateDirectory(tools);

        void Script(string name, string script)
        {
            string file = Path.Combine(tools, name);
            File.WriteAllText(file, script);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                             | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                                             UnixFileMode.GroupExecute);
        }

        Script("benchmark", """
                            #!/bin/sh
                            scenario=""
                            while [ "$#" -gt 0 ]; do
                              if [ "$1" = "--scenario" ]; then scenario="$2"; shift 2; else shift; fi
                            done
                            if [ "$scenario" = "idle-burst" ]; then
                              echo "$scenario p50= 40 p99= 90 max= 100"
                            else
                              echo "$scenario median= 1 min= 0.8 max= 1.2 throughput= 3"
                            fi
                            """);
        Script("cmake", """
                        #!/bin/sh
                        if [ "$1" = "--build" ]; then
                          mkdir -p "$2"
                          cp "$CMW_FAKE_BENCH" "$2/dagflow-stress-bench"
                          chmod +x "$2/dagflow-stress-bench"
                          exit 0
                        fi
                        while [ "$#" -gt 0 ]; do
                          if [ "$1" = "-B" ]; then mkdir -p "$2"; exit 0; fi
                          shift
                        done
                        exit 2
                        """);
        Script("perf", """
                       #!/bin/sh
                       mode="$1"
                       out=""
                       while [ "$#" -gt 0 ]; do
                         if [ "$1" = "-o" ]; then out="$2"; break; fi
                         shift
                       done
                       case "$mode" in
                         version) echo "fake perf";;
                         record) printf 'fake perf data\n' > "$out";;
                         stat)
                           cat > "$out" <<'EOF'
                       100;;cycles:u;;100.00
                       200;;instructions:u;;100.00
                       300;;branches:u;;100.00
                       4;;branch-misses:u;;100.00
                       500;;cache-references:u;;100.00
                       6;;cache-misses:u;;100.00
                       7;;page-faults:u;;100.00
                       EOF
                           ;;
                         report) echo "fake report";;
                         script) echo "bench 100/100 1.0: 10 cycles:u: 7f Work::execute (bench)";;
                         *) exit 2;;
                       esac
                       """);
        Script("taskset", """
                          #!/bin/sh
                          [ "$1" = "-c" ] || exit 2
                          shift 2
                          exec "$@"
                          """);
        Script("lscpu", "#!/bin/sh\necho fake-cpu\n");
        Script("clang++", "#!/bin/sh\necho fake-clang\n");
        Script("ldd", "#!/bin/sh\necho fake-ldd\n");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousBench = Environment.GetEnvironmentVariable("CMW_FAKE_BENCH");
        string output = Path.Combine(root, "historical-full-run");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + previousPath);
            Environment.SetEnvironmentVariable("CMW_FAKE_BENCH", Path.Combine(tools, "benchmark"));
            DagFlowHistoricalOptions options = new(source, output, 1, 1, 1);
            if (await DagFlowHistoricalCapture.RunAsync(options, CancellationToken.None) != 0)
            {
                throw new System.Diagnostics.UnreachableException("Historical capture fixture failed.");
            }

            JsonArray summary = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "summary.json"), CancellationToken.None))!.AsArray();
            if (summary.Count != 24
                || !File.Exists(Path.Combine(output, "profiles", "o3-1-idle-burst", "flamegraph.svg"))
                || !File.Exists(Path.Combine(output, "profiles-fp", "o3-1-idle-burst", "flamegraph.svg")))
            {
                throw new System.Diagnostics.UnreachableException("Historical capture omitted measurements or profiles.");
            }

            int before = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "commands.json"), CancellationToken.None))!.AsArray().Count;
            _ = await DagFlowHistoricalCapture.RunAsync(options with { Resume = true }, CancellationToken.None);
            int after = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "commands.json"), CancellationToken.None))!.AsArray().Count;
            if (before != after)
            {
                throw new System.Diagnostics.UnreachableException("Historical resume repeated successful effects.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("CMW_FAKE_BENCH", previousBench);
        }
    }
}

internal sealed class FakeProcess : IProfileProcess
{
    public List<(string Executable, string[] Args)> Calls { get; } = [];
    public bool FailTarget { get; init; }

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        string[] args = [.. arguments];
        Calls.Add((executable, args));
        if (FailTarget && executable != "wpr")
        {
            throw new InvalidOperationException("target failed");
        }

        if (executable == "perf")
        {
            string path = args[Array.IndexOf(args, "-o") + 1];
            await File.WriteAllTextAsync(path, "perf fixture", cancellationToken);
        }

        if (executable == "xcrun")
        {
            string path = args[Array.IndexOf(args, "--output") + 1];
            _ = Directory.CreateDirectory(path);
        }

        if (executable == "wpr" && args[0] == "-stop")
        {
            await File.WriteAllTextAsync(args[1], "ETL fixture", cancellationToken);
        }

        return new CommandResult(0, "", false, executable == "wpr" ? null : 42);
    }
}

internal sealed class FakeTracerptProcess(string fixture) : IProfileProcess
{
    public string[]? Arguments { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        if (executable != "tracerpt")
        {
            throw new System.Diagnostics.UnreachableException("Unexpected ETL converter.");
        }

        Arguments = [.. arguments];
        File.Copy(fixture, arguments[Array.IndexOf(Arguments, "-o") + 1], overwrite: true);
        return Task.FromResult(new CommandResult(0, "", false));
    }
}