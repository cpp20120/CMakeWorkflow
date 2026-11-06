using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.Core;
using CMakeWorkflow.Core.Persistence;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Historical;
#pragma warning restore IDE0130

// The old one-off stress-harness protocol lives in this DagFlow extension,
// not in the workflow planner or the general profiler backend.
public static class DagFlowHistoricalCapture
{
    private static readonly string[] Scenarios =
    [
        "external-contention", "hot-shard-skew", "local-overflow",
        "nested-helping", "mixed-chaos", "idle-burst"
    ];

    private static readonly string[] Events =
    [
        "cycles:u", "instructions:u", "branches:u", "branch-misses:u",
        "cache-references:u", "cache-misses:u", "page-faults"
    ];

    private static readonly string[] Variants = ["o3", "o3-lto"];

    /// <summary>Executes historical capture through the existing journal-backed command dispatcher.</summary>
    /// <remarks>Cancellation stops this request-owned capture; it does not roll back commands or permit replay of unknown effects.</remarks>
    public static Task<int> RunAsync(DagFlowHistoricalOptions options, CancellationToken ct) =>
        RunCoreAsync(options, null, ct);

    /// <summary>Owns a single run lease, frozen input snapshot and persisted measurement order.</summary>
    /// <remarks>
    /// The existing perf permission probe precedes the run lease. Source/set validation and durable schedule
    /// persistence precede measurement dispatch. Configure/build and capture diagnostics retain their existing
    /// prerequisites. Exceptions leave evidence for fail-closed journal recovery, not an assumed terminal success.
    /// </remarks>
    /// <param name="options">Capture inputs; the seed list is copied before asynchronous work.</param>
    /// <param name="expectedSourceIdentity">Batch snapshot hash to require, or null for the first/single experiment.</param>
    /// <param name="ct">Capture execution lifetime; journal safety commits remain owned by the dispatcher.</param>
    internal static async Task<int> RunCoreAsync(DagFlowHistoricalOptions options, string? expectedSourceIdentity,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { Seeds = HistoricalSchedule.Seeds(options) };
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Historical perf protocol requires Linux.");
        }

        HistoricalSchedule.Validate(options);
        if (HistoricalSchedule.Seeds(options).Length > 1)
        {
            return await HistoricalExperiments.RunAsync(options, ct);
        }

        string root = Path.GetFullPath(options.SourceDirectory);
        string output = Path.GetFullPath(options.OutputDirectory);
        string source = Path.Combine(output, "source");
        string manifestPath = Path.Combine(output, "manifest.json");
        if (!File.Exists(Path.Combine(root, "bench/stress_harness.cpp")))
        {
            throw new ArgumentException("Source is not a DagFlow stress-harness tree.");
        }

        if (!options.Resume && (File.Exists(manifestPath) || Directory.Exists(source)))
        {
            throw new IOException("Output already contains a measurement; choose a fresh directory or --resume.");
        }

        if (options.Resume && !File.Exists(manifestPath))
        {
            throw new IOException("Cannot resume without a manifest.");
        }

        string cpus = (await File.ReadAllLinesAsync("/proc/self/status", ct))
                       .FirstOrDefault(x => x.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))?
                       .Split(':', 2)[1].Trim()
                   ?? throw new IOException("Host CPU allowance unavailable.");
        string probe = Path.Combine(Path.GetTempPath(), "cmw-perf-probe-" + Guid.NewGuid().ToString("N") + ".data");
        try
        {
            CommandResult permission = await CommandExecution.RunAsync("perf",
                [
                    "record", "-o", probe, "-e", "cycles:u", "-c", "10000",
                    "--call-graph", "dwarf,16384", "--",
                    "taskset", "-c", cpus, "true"
                ], root,
                TimeSpan.FromSeconds(10), new Dictionary<string, string?> { ["LC_ALL"] = "C" }, ct);
            if (permission.TimedOut || permission.ExitCode != 0 || !File.Exists(probe))
            {
                throw new InvalidOperationException("Historical perf recording unavailable: "
                                                    + permission.Output[^Math.Min(1500, permission.Output.Length)..]);
            }
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }

        _ = Directory.CreateDirectory(output);
        // Two independent historical captures must never share the command journal,
        // source snapshot or measurement directory. The lock covers all writes.
        await using FileStream runLease = new(Path.Combine(output, ".historical-capture.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _ = Directory.CreateDirectory(Path.Combine(output, "logs"));
        if (!options.Resume)
        {
            _ = Directory.CreateDirectory(source);
            foreach (string? name in new[] { "include", "src", "cmake", "bench" })
            {
                CopyDirectory(Path.Combine(root, name), Path.Combine(source, name));
            }

            File.Copy(Path.Combine(root, "CMakeLists.txt"), Path.Combine(source, "CMakeLists.txt"));
        }

        Dictionary<string, string?> environment = new() { ["LC_ALL"] = "C", ["DEBUGINFOD_URLS"] = "" };
        string commandsPath = Path.Combine(output, "commands.json");
        JsonArray commands = options.Resume && File.Exists(commandsPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(commandsPath, ct))?.AsArray()
              ?? throw new InvalidDataException("Invalid command journal.")
            : [];

        // The specialized experiment declares its steps; the generic command
        // dispatcher owns write-ahead admission, process lifetime, evidence and
        // fail-closed resume. Progress is an observer of that same journal.
        using HistoricalProgress progress = new(options.TimingTrials, options.StatTrials, options.Resume);
        WorkflowCommandDispatcher dispatcher = new(output, commands, options.Resume,
            new Dictionary<string, string>(StringComparer.Ordinal)
            { ["LC_ALL"] = "C", ["DEBUGINFOD_URLS"] = "" }, progress.Observe);

        static string? DeclaredOutput(string executable, string[] argv)
        {
            // perf creates outputs independently of our captured stdout/stderr.
            int begin = 0;
            if (Path.GetFileName(executable) == "taskset")
            {
                int nested = Array.IndexOf(argv, "perf");
                if (nested < 0)
                {
                    return null;
                }

                begin = nested + 1;
            }
            else if (Path.GetFileName(executable) != "perf")
            {
                return null;
            }

            if (argv.Length <= begin || argv[begin] is not ("stat" or "record"))
            {
                return null;
            }

            int index = Array.IndexOf(argv, "-o", begin + 1);
            return index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
        }

        Task<string> Run(string label, string executable, IEnumerable<string> arguments,
            int timeoutSeconds = 300)
        {
            string[] argv = [.. arguments];
            return dispatcher.RunAsync(label, executable, argv,
                DeclaredOutput(executable, argv), timeoutSeconds, ct);
        }

        JsonObject sourceHashes = [];
        foreach (string? file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            sourceHashes[Path.GetRelativePath(source, file).Replace('\\', '/')] =
                await CommandExecution.Sha256Async(file, ct);
        }

        if (!options.Resume)
        {
            foreach ((string? relative, JsonNode? hash) in sourceHashes)
            {
                if (await CommandExecution.Sha256Async(Path.Combine(root, relative), ct)
                    != hash!.GetValue<string>())
                {
                    throw new InvalidDataException("Source changed while snapshotting: " + relative);
                }
            }
        }

        JsonObject manifest = options.Resume
            ? JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, ct))?.AsObject()
              ?? throw new InvalidDataException("Invalid historical manifest.")
            : new JsonObject
            {
                ["started_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "bench/stress_harness.cpp only; scenario selection; original workload counts",
                ["cpus"] = cpus,
                ["pin_threads"] = false,
                ["allocator"] = "mimalloc",
                ["timing_trials"] = options.TimingTrials,
                ["stat_trials"] = options.StatTrials,
                ["jobs"] = options.Jobs,
                ["events"] = new JsonArray([.. Events.Select(x => (JsonNode?)JsonValue.Create(x))]),
                ["record_event"] = "cycles:u",
                ["call_graph"] = "dwarf,16384",
                ["frequency_locked"] = false,
                ["build"] = "static DagFlow, dynamic mimalloc; O3 -g -DNDEBUG; lld for both",
                ["source_sha256"] = sourceHashes.DeepClone(),
                ["ordering"] = options.ShuffleAlgorithm == "python-legacy" &&
                    HistoricalSchedule.Seeds(options).Single() == 20260930 &&
                    options.ScheduleStrategy == "random-interleaved" && options.ShuffleScope == "block"
                    ? "Python random.Random(20260930) shuffle schedule" : "Saved historical schedule/v1"
            };
        if (options.Resume)
        {
            if (manifest["source_sha256"]?.ToJsonString() != sourceHashes.ToJsonString()
                || manifest["cpus"]?.GetValue<string>() != cpus
                || manifest["timing_trials"]?.GetValue<int>() != options.TimingTrials
                || manifest["stat_trials"]?.GetValue<int>() != options.StatTrials
                || (manifest["jobs"] is not null && manifest["jobs"]!.GetValue<int>() != options.Jobs))
            {
                throw new InvalidDataException("Cannot resume changed source, affinity or trial counts.");
            }

            foreach (JsonNode? binary in manifest["binaries"]?.AsObject().Select(x => x.Value)
                                   ?? [])
            {
                if (await CommandExecution.Sha256Async(binary!["path"]!.GetValue<string>(), ct)
                    != binary["sha256"]!.GetValue<string>())
                {
                    throw new InvalidDataException("Cannot resume modified binary.");
                }
            }
        }

        string sourceIdentity = HistoricalSchedule.Hash(sourceHashes.ToJsonString());
        if (expectedSourceIdentity is not null && expectedSourceIdentity != sourceIdentity)
        {
            throw new InvalidDataException("Seed experiments require an identical frozen source/workload set.");
        }
        JsonObject schedule;
        if (options.Resume)
        {
            schedule = await HistoricalSchedule.LoadAsync(options, sourceIdentity, manifest, commands, ct);
        }
        else
        {
            schedule = HistoricalSchedule.Create(options, sourceIdentity);
            await HistoricalSchedule.SaveAsync(Path.Combine(output, "schedule.json"), schedule, ct);
            manifest["schedule_sha256"] = HistoricalSchedule.Hash(schedule.ToJsonString() + "\n");
        }
        manifest["schedule_file"] = "schedule.json";
        manifest["scheduling"] = new JsonObject
        {
            ["identity"] = schedule["identity"]!.DeepClone(),
            ["prngVersion"] = schedule["prngVersion"]!.DeepClone(),
            ["measurementSetSha256"] = schedule["measurementSetSha256"]!.DeepClone(),
            ["statisticalInterpretation"] = "Seeds/block order are experimental factors, not independent samples."
        };
        async Task SaveManifest() => await DurableByteWriter.WriteAsync(manifestPath,
            Encoding.UTF8.GetBytes(manifest.ToJsonString() + "\n"), ct);

        // Persist identity and the complete future order before command dispatch. On resume the same
        // journal verifies observations/builds; it refuses unknown effects rather than rerunning them.
        await SaveManifest();
        string cpuEvidence = await Run("cpu", "lscpu", []);
        string compilerEvidence = await Run("compiler", "clang++", ["--version"]);
        string perfEvidence = await Run("perf-version", "perf", ["version", "--build-options"]);
        if (!options.Resume)
        {
            manifest["cpu"] = cpuEvidence;
            manifest["compiler"] = compilerEvidence;
            manifest["perf"] = perfEvidence;
        }
        await SaveManifest();
        Dictionary<string, string> binaries = new(StringComparer.Ordinal);
        foreach (string profile in Variants)
        {
            string build = Path.Combine(output, "build", profile);
            _ = await Run(profile + "-configure", "cmake", [
                "-S", source, "-B", build, "-G", "Ninja",
                "-DCMAKE_CXX_COMPILER=clang++", "-DCMAKE_BUILD_TYPE=Release",
                "-DCMAKE_CXX_FLAGS_RELEASE=-O3 -g -DNDEBUG",
                "-DDAGFLOW_BUILD_SHARED=OFF", "-DDAGFLOW_BUILD_STATIC=ON", "-DDAGFLOW_INSTALL=OFF",
                "-DDAGFLOW_BUILD_EXAMPLES=OFF", "-DDAGFLOW_BUILD_STRESS_BENCH=ON",
                "-DDAGFLOW_BUILD_TESTS=OFF", "-DDAGFLOW_USE_LLD=ON",
                "-DDAGFLOW_ENABLE_NATIVE=OFF", "-DDAGFLOW_PGO_MODE=none",
                "-DDAGFLOW_ALLOCATOR=mimalloc", "-DDAGFLOW_LTO_MODE=" + (profile == "o3" ? "none" : "full")
            ]);
            _ = await Run(profile + "-build", "cmake",
            [
                "--build", build, "--target", "dagflow_stress_bench", "-j",
                options.Jobs.ToString(CultureInfo.InvariantCulture)
            ]);
            string binary = Path.Combine(build, "dagflow-stress-bench");
            if (!File.Exists(binary))
            {
                throw new FileNotFoundException("Stress benchmark missing.", binary);
            }

            binaries[profile] = binary;
            _ = await Run(profile + "-ldd", "ldd", [binary]);
        }

        manifest["binaries"] = new JsonObject();
        foreach ((string? profile, string? binary) in binaries)
        {
            manifest["binaries"]![profile] = new JsonObject
            {
                ["path"] = binary,
                ["sha256"] = await CommandExecution.Sha256Async(binary, ct)
            };
        }

        await SaveManifest();
        List<JsonObject> timings = [];
        List<JsonObject> counters = [];
        string[] Invocation(string profile, int workers, string scenario) =>
        [
            "-c", cpus, binaries[profile], "--workers", workers.ToString(CultureInfo.InvariantCulture),
            "--scenario", scenario
        ];

        foreach (JsonNode? item in schedule["entries"]!.AsArray())
        {
            ct.ThrowIfCancellationRequested();
            JsonObject entry = item!.AsObject();
            string kind = entry["kind"]!.GetValue<string>();
            string profile = entry["profile"]!.GetValue<string>();
            int workers = entry["workers"]!.GetValue<int>();
            string scenario = entry["scenario"]!.GetValue<string>();
            string label = entry["label"]!.GetValue<string>();
            JsonObject row;
            if (kind == "time")
            {
                string stdout = await Run(label, "taskset", Invocation(profile, workers, scenario));
                row = ParseTiming(stdout, scenario);
            }
            else
            {
                string csv = Path.Combine(output, label + ".csv");
                _ = await Run(label, "perf", new[]
                {
                    "stat", "-x", ";", "-o", csv, "-e", string.Join(",", Events), "--", "taskset"
                }.Concat(Invocation(profile, workers, scenario)));
                (JsonObject counts, JsonObject running) = ParseStat(await File.ReadAllTextAsync(csv, ct));
                row = new JsonObject { ["counts"] = counts, ["running_pct"] = running };
            }
            row["profile"] = profile;
            row["workers"] = workers;
            row["scenario"] = scenario;
            row["trial"] = entry["trial"]!.DeepClone();
            row["schedule_order"] = entry["order"]!.DeepClone();
            row["block_id"] = entry["block"]!.DeepClone();
            row["pair_id"] = entry["pair"]!.DeepClone();
            row["seed"] = schedule["identity"]!["seed"]!.DeepClone();
            row["prng"] = schedule["identity"]!["algorithm"]!.DeepClone();
            row["prng_version"] = schedule["prngVersion"]!.DeepClone();
            row["schedule_strategy"] = schedule["identity"]!["strategy"]!.DeepClone();
            row["shuffle_scope"] = schedule["identity"]!["scope"]!.DeepClone();
            row["measurement_set_sha256"] = schedule["measurementSetSha256"]!.DeepClone();
            if (kind == "time")
            {
                timings.Add(row);
                await WriteLines(Path.Combine(output, "timings.jsonl"), timings, ct);
            }
            else
            {
                counters.Add(row);
                await WriteLines(Path.Combine(output, "counters.jsonl"), counters, ct);
            }
        }

        manifest["sampling"] = "fixed period max(10000, median stat cycles / 1500); weighted by period";
        manifest["profile_periods"] = new JsonObject();
        foreach (string profile in Variants)
        {
            foreach (int workers in new[] { 1, 4 })
            {
                foreach (string scenario in Scenarios)
                {
                    string label = profile + "-" + workers + "-" + scenario;
                    string directory = Path.Combine(output, "profiles", label);
                    _ = Directory.CreateDirectory(directory);
                    IEnumerable<double> cycles = counters.Where(row => RowIs(row, profile, workers, scenario))
                        .Select(row => row["counts"]!["cycles:u"]!.GetValue<double>());
                    int period = Math.Max(10000, (int)(Median(cycles) / 1500));
                    manifest["profile_periods"]![label] = period;
                    string data = Path.Combine(directory, "perf.data");
                    _ = await Run("periodic-record-" + label, "perf",
                        new[]
                        {
                    "record", "-o", data, "-e", "cycles:u", "-c",
                    period.ToString(CultureInfo.InvariantCulture), "--call-graph", "dwarf,16384",
                    "--", "taskset"
                        }.Concat(Invocation(profile, workers, scenario)));
                    string flat = await Run("periodic-flat-" + label, "perf",
            [
                "report", "-i", data, "--stdio", "--no-children", "--sort", "symbol,dso",
                "--percent-limit", "0.5"
            ]);
                    await File.WriteAllTextAsync(Path.Combine(directory, "hotspots.txt"), flat, ct);
                    string graph = await Run("periodic-callgraph-" + label, "perf",
            [
                "report", "-i", data, "--stdio", "--children", "--call-graph",
                "graph,0.5,caller", "--percent-limit", "0.5"
            ]);
                    await File.WriteAllTextAsync(Path.Combine(directory, "callgraph.txt"), graph, ct);
                    string script = await Run("periodic-flame-" + label, "perf",
                ["script", "-i", data, "-F", "comm,pid,tid,time,period,event,ip,sym,dso"]);
                    await SaveFlamegraphAsync(script, Path.Combine(directory, "flamegraph.json"), ct);
                }
            }
        }

        await SaveManifest();
        JsonArray summary = Summarize(timings, counters);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), summary.ToJsonString() + "\n", ct);
        await WriteCsv(Path.Combine(output, "summary.csv"), summary, ct);
        await CaptureFramePointers(output, source, cpus, summary, manifest, Run, SaveManifest, ct);
        progress.Phase("Rendering historical SVG/HTML report");
        int result = await DagFlowHistoricalReport.RenderAsync(output, ct);
        if (result == 0)
        {
            manifest["finished_utc"] ??= DateTimeOffset.UtcNow.ToString("O");
            await SaveManifest();
            JsonObject hashes = [];
            foreach (string name in new[] { "manifest.json", "schedule.json", "timings.jsonl", "counters.jsonl", "summary.json" })
            {
                hashes[name] = await CommandExecution.Sha256Async(Path.Combine(output, name), ct);
            }
            await HistoricalSchedule.SaveAsync(Path.Combine(output, "historical-completion.json"),
                new JsonObject { ["schemaVersion"] = 1, ["status"] = "succeeded", ["sha256"] = hashes }, ct);
        }
        progress.Phase("Historical capture complete");
        return result;
    }

    private static void CopyDirectory(string from, string to)
    {
        _ = Directory.CreateDirectory(to);
        foreach (string directory in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }

        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static async Task WriteLines(string path, IEnumerable<JsonObject> rows, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, string.Join("\n", rows.Select(x => x.ToJsonString())) + "\n", ct);

    private static bool RowIs(JsonObject row, string profile, int workers, string scenario) =>
        row["profile"]!.GetValue<string>() == profile
        && row["workers"]!.GetValue<int>() == workers
        && row["scenario"]!.GetValue<string>() == scenario;

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0
            ? throw new InvalidDataException("Missing measurement rows.")
            : sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>Converts a scenario timing line into the archived experiment units.</summary>
    /// <remarks>
    /// idle-burst reports p50/p99/max directly in microseconds. Other scenarios convert median/min/max from
    /// milliseconds to microseconds and preserve throughput as million tasks per second. Missing scenario
    /// lines, required fields or malformed numbers fail rather than substituting timing samples.
    /// </remarks>
    internal static JsonObject ParseTiming(string stdout, string scenario)
    {
        string line = stdout.Split('\n').FirstOrDefault(x => x.StartsWith(scenario + " ", StringComparison.Ordinal))
                   ?? throw new InvalidDataException("Missing scenario timing: " + scenario);
        Dictionary<string, double> values = Regex.Matches(line, @"([a-z0-9]+)=\s*([0-9.]+)")
            .ToDictionary(x => x.Groups[1].Value, x => double.Parse(x.Groups[2].Value,
                CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return scenario == "idle-burst"
            ? new JsonObject
            {
                ["time_us"] = values["p50"],
                ["p99_us"] = values["p99"],
                ["max_us"] = values["max"]
            }
            : new JsonObject
            {
                ["time_us"] = 1000 * values["median"],
                ["min_us"] = 1000 * values["min"],
                ["max_us"] = 1000 * values["max"],
                ["mtasks_s"] = values["throughput"]
            };
    }

    /// <summary>Parses the required perf-stat event counts and optional running percentages.</summary>
    /// <remarks>
    /// Normalizes page-faults:u to page-faults. Unavailable/nonnumeric required counters and incomplete event
    /// sets fail. Running values remain percentages as reported; they are not fractions, elapsed time or
    /// proof that counters were continuously scheduled. No process is launched by this parser.
    /// </remarks>
    internal static (JsonObject Counts, JsonObject Running) ParseStat(string text)
    {
        JsonObject counts = [];
        JsonObject running = [];
        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.TrimEnd('\r').Split(';');
            if (fields.Length < 3)
            {
                continue;
            }

            string eventName = fields[2] == "page-faults:u" ? "page-faults" : fields[2];
            if (!Events.Contains(eventName))
            {
                continue;
            }

            if (fields[0].StartsWith('<') || !double.TryParse(fields[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value))
            {
                throw new InvalidDataException("Unavailable perf counter: " + eventName);
            }

            counts[eventName] = value;
            if (fields.Length > 4 && double.TryParse(fields[4].TrimEnd('%'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double fraction))
            {
                running[eventName] = fraction;
            }
        }

        return counts.Count != Events.Length ? throw new InvalidDataException("Incomplete perf counters.") : ((JsonObject Counts, JsonObject Running))(counts, running);
    }

    internal static JsonArray Summarize(IReadOnlyList<JsonObject> timings, IReadOnlyList<JsonObject> counters)
    {
        JsonArray result = [];
        foreach (string profile in Variants)
        {
            foreach (int workers in new[] { 1, 4 })
            {
                foreach (string scenario in Scenarios)
                {
                    JsonObject[] selected = [.. timings.Where(row => RowIs(row, profile, workers, scenario))];
                    JsonObject[] stats = [.. counters.Where(row => RowIs(row, profile, workers, scenario))];
                    double[] time = [.. selected.Select(row => row["time_us"]!.GetValue<double>())];
                    JsonObject row = new()
                    {
                        ["profile"] = profile,
                        ["workers"] = workers,
                        ["scenario"] = scenario,
                        ["time_us"] = Median(time),
                        ["min_trial_us"] = time.Min(),
                        ["max_trial_us"] = time.Max()
                    };
                    if (scenario == "idle-burst")
                    {
                        row["p99_us"] = Median(selected.Select(x => x["p99_us"]!.GetValue<double>()));
                    }

                    foreach (string eventName in Events)
                    {
                        row[eventName] = Median(stats.Select(x => x["counts"]![eventName]!.GetValue<double>()));
                    }

                    row["ipc"] = Median(stats.Select(x => x["counts"]!["instructions:u"]!.GetValue<double>()
                                                  / x["counts"]!["cycles:u"]!.GetValue<double>()));
                    row["branch_miss_pct"] = Median(stats.Select(x => 100 *
                                                                      x["counts"]!["branch-misses:u"]!.GetValue<double>()
                                                                      / x["counts"]!["branches:u"]!.GetValue<double>()));
                    row["cache_miss_pct"] = Median(stats.Select(x => 100 *
                                                                     x["counts"]!["cache-misses:u"]!.GetValue<double>()
                                                                     / x["counts"]!["cache-references:u"]!.GetValue<double>()));
                    row["min_counter_running_pct"] = stats.SelectMany(x => x["running_pct"]!.AsObject())
                        .Select(x => x.Value!.GetValue<double>()).DefaultIfEmpty(0).Min();
                    if (profile == "o3-lto")
                    {
                        Dictionary<int, double> before = timings.Where(x => RowIs(x, "o3", workers, scenario))
                            .ToDictionary(x => x["trial"]!.GetValue<int>(), x => x["time_us"]!.GetValue<double>());
                        double[] changes = [.. selected.Select(x => 100 *
                                                   ((x["time_us"]!.GetValue<double>() /
                                                       before[x["trial"]!.GetValue<int>()]) - 1))];
                        row["paired_time_change_pct"] = Median(changes);
                        row["min_paired_change_pct"] = changes.Min();
                        row["max_paired_change_pct"] = changes.Max();
                    }

                    result.Add((JsonNode?)row);
                }
            }
        }

        return result;
    }

    private static async Task WriteCsv(string path, JsonArray rows, CancellationToken ct)
    {
        string[] columns = [.. rows.SelectMany(x => x!.AsObject().Select(pair => pair.Key)).Distinct(StringComparer.Ordinal)];
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        StringBuilder csv = new(string.Join(",", columns.Select(Escape)) + "\n");
        foreach (JsonNode? row in rows)
        {
            _ = csv.Append(string.Join(",", columns.Select(key =>
                Escape(row![key]?.ToString() ?? "")))).Append('\n');
        }

        await File.WriteAllTextAsync(path, csv.ToString(), ct);
    }

    private static async Task SaveFlamegraphAsync(string stdout, string path, CancellationToken ct)
    {
        JsonObject tree = DagFlowHistoricalFlamegraph.Parse(new StringReader(stdout), ct);
        string json = tree.ToJsonString() + "\n";
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, json, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CaptureFramePointers(string output, string source,
        string cpus, JsonArray summary,
        JsonObject manifest, Func<string, string, IEnumerable<string>, int, Task<string>> run,
        Func<Task> saveManifest, CancellationToken ct)
    {
        JsonObject fpBinaries = [];
        foreach (string profile in Variants)
        {
            string build = Path.Combine(output, "build", profile + "-fp");
            _ = await run("fp-configure-" + profile, "cmake", [
                "-S", source, "-B", build, "-G", "Ninja",
                "-DCMAKE_CXX_COMPILER=clang++", "-DCMAKE_BUILD_TYPE=Release",
                "-DCMAKE_CXX_FLAGS_RELEASE=-O3 -g -DNDEBUG -fno-omit-frame-pointer -mno-omit-leaf-frame-pointer",
                "-DDAGFLOW_BUILD_SHARED=OFF", "-DDAGFLOW_BUILD_STATIC=ON", "-DDAGFLOW_INSTALL=OFF",
                "-DDAGFLOW_BUILD_EXAMPLES=OFF", "-DDAGFLOW_BUILD_STRESS_BENCH=ON",
                "-DDAGFLOW_BUILD_TESTS=OFF", "-DDAGFLOW_USE_LLD=ON",
                "-DDAGFLOW_ENABLE_NATIVE=OFF", "-DDAGFLOW_PGO_MODE=none",
                "-DDAGFLOW_ALLOCATOR=mimalloc", "-DDAGFLOW_LTO_MODE=" + (profile == "o3" ? "none" : "full")
            ], 300);
            _ = await run("fp-build-" + profile, "cmake",
                ["--build", build, "--target", "dagflow_stress_bench", "-j", "4"], 300);
            string binary = Path.Combine(build, "dagflow-stress-bench");
            fpBinaries[profile] = new JsonObject
            {
                ["path"] = binary,
                ["sha256"] = await CommandExecution.Sha256Async(binary, ct)
            };
        }

        foreach (string profile in Variants)
        {
            foreach (int workers in new[] { 1, 4 })
            {
                foreach (string scenario in Scenarios)
                {
                    string label = profile + "-" + workers + "-" + scenario;
                    JsonObject row = summary.Select(x => x!.AsObject()).Single(x => RowIs(x, profile, workers, scenario));
                    int period = Math.Max(10000, (int)(row["cycles:u"]!.GetValue<double>() / 1500));
                    string directory = Path.Combine(output, "profiles-fp", label);
                    _ = Directory.CreateDirectory(directory);
                    string data = Path.Combine(directory, "perf.data");
                    string binary = fpBinaries[profile]!["path"]!.GetValue<string>();
                    _ = await run("fp-record-" + label, "taskset",
                    [
                        "-c", cpus, "perf", "record", "-o", data, "-e", "cycles:u", "-c",
                period.ToString(CultureInfo.InvariantCulture),
                "--call-graph", "fp", "--", binary, "--workers",
                workers.ToString(CultureInfo.InvariantCulture), "--scenario", scenario
                    ], 300);
                    string flat = await run("fp-hotspots-" + label, "perf",
            [
                "report", "-i", data, "--stdio", "--percent-limit", "0.5",
                "--no-children", "--sort", "symbol,dso"
            ], 300);
                    await File.WriteAllTextAsync(Path.Combine(directory, "hotspots.txt"), flat, ct);
                    string graph = await run("fp-callgraph-" + label, "perf",
            [
                "report", "-i", data, "--stdio", "--percent-limit", "0.5",
                "--children", "--call-graph", "graph,0.5,caller"
            ], 300);
                    await File.WriteAllTextAsync(Path.Combine(directory, "callgraph.txt"), graph, ct);
                    string script = await run("fp-flame-" + label, "perf",
                ["script", "-i", data, "-F", "comm,pid,tid,time,period,event,ip,sym,dso"], 300);
                    await SaveFlamegraphAsync(script, Path.Combine(directory, "flamegraph.json"), ct);
                }
            }
        }

        manifest["diagnostic_frame_pointers"] = new JsonObject
        {
            ["flags"] = "-fno-omit-frame-pointer -mno-omit-leaf-frame-pointer",
            ["call_graph"] = "fp",
            ["cpus"] = cpus,
            ["purpose"] = "supplemental stacks only; excluded from primary timing/counter tables",
            ["binaries"] = fpBinaries
        };
        await saveManifest();
    }
}
