using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;

namespace CMakeWorkflow.Profiling;

public static class ProfileCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct,
        Action<string>? onRunDirectory = null)
    {
        if (args.Length == 0) throw new ArgumentException("Usage: profile <run|analyze> ...");
        if (args[0] == "analyze")
        {
            if (args.Length != 2) throw new ArgumentException("profile analyze RUN_DIRECTORY");
            return await ProfileAnalyzer.AnalyzeAsync(args[1], ct);
        }
        if (args[0] != "run") throw new ArgumentException("Usage: profile <run|analyze> ...");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var variants = new List<(string Name, string BuildDirectory)>();
        var targetArgs = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                targetArgs.AddRange(args[(i + 1)..]);
                break;
            }
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid profile option: {args[i]}");
            if (args[i] is not ("--build-dir" or "--target" or "--backend" or "--mode"
                or "--results-dir" or "--source-dir" or "--preset" or "--config" or "--timeout-seconds"
                or "--variant" or "--repeats" or "--campaign" or "--o3-build-dir"
                or "--lto-build-dir" or "--output"))
                throw new ArgumentException($"Unknown profile option: {args[i]}");
            if (args[i] == "--variant")
            {
                var pair = args[i + 1].Split('=', 2);
                if (pair.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(pair[0], "^[a-z][a-z0-9_]*$")
                    || string.IsNullOrWhiteSpace(pair[1]))
                    throw new ArgumentException("--variant requires name=BUILD_DIR (lowercase name).");
                variants.Add((pair[0], Path.GetFullPath(pair[1])));
                i++;
                continue;
            }
            if (!options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Duplicate profile option: {args[i]}");
            i++;
        }
        string Required(string key) => options.GetValueOrDefault(key)
            ?? throw new ArgumentException($"{key} is required.");
        if (options.TryGetValue("--campaign", out var campaign))
        {
            if (campaign != "main-o3-lto" || variants.Count != 0 || options.ContainsKey("--build-dir"))
                throw new ArgumentException("--campaign main-o3-lto requires two named build directories.");
            variants.Add(("o3", Path.GetFullPath(Required("--o3-build-dir"))));
            variants.Add(("lto", Path.GetFullPath(Required("--lto-build-dir"))));
        }
        else if (variants.Count == 0)
            variants.Add(("main", Path.GetFullPath(Required("--build-dir"))));
        else if (options.ContainsKey("--build-dir") || options.ContainsKey("--o3-build-dir")
            || options.ContainsKey("--lto-build-dir"))
            throw new ArgumentException("Use --variant or --build-dir, not both.");
        if (variants.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != variants.Count)
            throw new ArgumentException("Profile variant names must be unique.");
        if (variants.Select(x => Path.GetFullPath(x.BuildDirectory))
            .Distinct(WorkflowPlanner.PathComparer).Count() != variants.Count)
            throw new ArgumentException("Profile variants require distinct build directories.");
        var target = options.TryGetValue("--target", out var targetOption) ? targetOption
            : campaign is not null ? "dagflow_stress_bench"
            : throw new ArgumentException("--target is required.");
        var output = Path.GetFullPath(options.GetValueOrDefault("--results-dir",
            options.GetValueOrDefault("--output", "out/profiles")));
        var repeatsText = options.GetValueOrDefault("--repeats", "3");
        if (!int.TryParse(repeatsText, out var repeats) || repeats is < 1 or > 100)
            throw new ArgumentException("--repeats must be between 1 and 100.");
        var source = options.TryGetValue("--source-dir", out var sourceOption)
            ? Path.GetFullPath(sourceOption) : null;
        var timeout = options.GetValueOrDefault("--timeout-seconds", "300");
        if (!double.TryParse(timeout, out var seconds) || seconds <= 0 || seconds > 86400)
            throw new ArgumentException("--timeout-seconds must be positive and at most one day.");
        Directory.CreateDirectory(output);
        var preset = options.GetValueOrDefault("--preset");
        if (preset is not null && source is null)
            source = Directory.GetCurrentDirectory();
        if (preset is not null && variants.Count != 1)
            throw new ArgumentException("--preset with multiple variants is ambiguous; configure each tree first.");
        var contexts = new Dictionary<string, WorkflowContext>(StringComparer.Ordinal);
        var nodes = new List<WorkflowNode>();
        foreach (var variant in variants)
        {
            contexts[variant.Name] = new WorkflowContext
            {
                BuildDirectory = variant.BuildDirectory, SourceDirectory = source,
                Configuration = options.GetValueOrDefault("--config"), ConfigurePreset = preset,
                Mode = preset is null ? "adopt" : "managed",
                ProvenancePolicy = preset is null ? "allowUnverified" : "strict"
            };
            if (preset is not null)
                nodes.Add(new WorkflowNode { Id = "configure", Kind = "configure", Context = variant.Name });
            nodes.Add(new WorkflowNode { Id = "build_" + variant.Name, Kind = "build",
                Context = variant.Name, Target = target,
                DependsOn = preset is null ? [] : ["configure"] });
        }
        var allBuilds = nodes.Where(x => x.Kind == "build").Select(x => x.Id).ToList();
        var previous = nodes.Last().Id;
        var firstTiming = true;
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            // Alternate variant order to reduce drift from thermal state and
            // background activity without mixing profiling into timing runs.
            var order = repeat % 2 == 1 ? variants : variants.AsEnumerable().Reverse();
            foreach (var variant in order)
            {
                var buildId = "build_" + variant.Name;
                var id = $"timing_{variant.Name}_{repeat}";
                nodes.Add(new WorkflowNode { Id = id, Kind = "run", From = buildId,
                    DependsOn = firstTiming ? allBuilds.ToList() : [previous],
                    Arguments = targetArgs.ToList(),
                    TimeoutSeconds = seconds, Resources = ["measurement-host"] });
                previous = id;
                firstTiming = false;
            }
        }
        var backend = options.GetValueOrDefault("--backend", "auto");
        var mode = options.GetValueOrDefault("--mode", "cpu");
        if (OperatingSystem.IsLinux() && (backend is "auto" or "perf") && mode != "counters")
        {
            foreach (var variant in variants)
            {
                var buildId = "build_" + variant.Name;
                var id = "counters_" + variant.Name;
                nodes.Add(ProfileNode(id, buildId, previous, targetArgs, seconds, timeout,
                    backend, "counters"));
                previous = id;
            }
        }
        foreach (var variant in variants)
        {
            var buildId = "build_" + variant.Name;
            var captureId = "capture_" + variant.Name;
            nodes.Add(ProfileNode(captureId, buildId, previous, targetArgs, seconds, timeout,
                backend, mode));
            previous = captureId;
        }
        var document = new WorkflowDocument
        {
            SchemaVersion = 2,
            Contexts = contexts,
            Nodes = nodes
        };
        var manifest = Path.Combine(output, "profile-workflow-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(manifest,
            JsonSerializer.Serialize(document, WorkflowJson.Default.WorkflowDocument) + "\n", ct);
        string? runDirectory = null;
        var result = await WorkflowCommand.ExecuteAsync("run", [manifest, "--results-dir", output], ct,
            new ProfileWorkflowExecutor(), directory =>
            { runDirectory = directory; onRunDirectory?.Invoke(directory); });
        if (result != 0 || runDirectory is null) return result;
        return await ProfileAnalyzer.AnalyzeAsync(runDirectory, ct);
    }

    private static WorkflowNode ProfileNode(string id, string buildId, string after,
        IReadOnlyList<string> arguments, double seconds, string timeout, string backend, string mode) =>
        new() { Id = id, Kind = "profile", From = buildId, DependsOn = [after],
            Arguments = arguments.ToList(), TimeoutSeconds = seconds + 180,
            Exclusive = true, Resources = ["measurement-host"],
            Options = new Dictionary<string, string> { ["backend"] = backend,
                ["mode"] = mode, ["captureTimeoutSeconds"] = timeout } };
}

public static class ProfileAnalyzer
{
    public static async Task<int> AnalyzeAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        var captures = Directory.GetFiles(directory, "*.results.json")
            .Order(StringComparer.Ordinal).ToArray();
        if (captures.Length == 0) throw new FileNotFoundException("No profile capture results found.", directory);
        var items = new JsonArray();
        foreach (var capture in captures)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(capture, ct));
            var root = document.RootElement;
            var backend = root.GetProperty("backend").GetString()!;
            var mode = root.GetProperty("mode").GetString()!;
            var raw = Path.GetFullPath(Path.Combine(directory, root.GetProperty("rawTrace").GetString()!));
            if (!raw.StartsWith(directory + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException($"Profile trace path escapes the run directory: {raw}");
            if (!File.Exists(raw) && !Directory.Exists(raw))
                throw new FileNotFoundException("Profile trace is missing.", raw);
            if (root.TryGetProperty("rawTraceSha256", out var expectedHash)
                && await ProfileArtifactHash.Sha256Async(raw, ct) != expectedHash.GetString())
                throw new IOException($"Profile trace changed after capture: {raw}");
            var item = new JsonObject
            {
                ["captureId"] = root.GetProperty("captureId").GetString(),
                ["backend"] = backend,
                ["mode"] = mode,
                ["rawTrace"] = Path.GetRelativePath(directory, raw),
                ["eventSource"] = root.GetProperty("eventSource").GetString(),
                ["crossPlatformMetricEquivalence"] = false
            };
            if (backend == "perf" && mode == "counters")
            {
                item["report"] = Path.GetRelativePath(directory, raw);
                var counters = new JsonObject();
                foreach (var (eventName, counter) in ReadPerfCounters(raw))
                    counters[eventName] = new JsonObject
                    {
                        ["value"] = JsonValue.Create(counter.Value, ProfileJson.Default.Double),
                        ["unit"] = counter.Unit
                    };
                item["counters"] = counters;
            }
            else if (backend == "perf")
            {
                var report = Path.Combine(directory, item["captureId"]!.GetValue<string>() + ".perf-report.txt");
                var result = await CommandExecution.RunAsync("perf",
                    ["report", "-i", raw, "--stdio", "--no-children", "--sort", "symbol,dso"],
                    directory, TimeSpan.FromMinutes(5), null, ct);
                ProfileEnvironment.RequireSuccess(result, "perf report");
                await File.WriteAllTextAsync(report, result.Output, ct);
                item["report"] = Path.GetRelativePath(directory, report);
            }
            else if (backend == "instruments")
            {
                var report = Path.Combine(directory, item["captureId"]!.GetValue<string>() + ".toc.xml");
                var result = await CommandExecution.RunAsync("xcrun",
                    ["xctrace", "export", raw, "--toc", "--output", report],
                    directory, TimeSpan.FromMinutes(5), null, ct);
                ProfileEnvironment.RequireSuccess(result, "xctrace export");
                item["report"] = Path.GetRelativePath(directory, report);
            }
            else if (backend == "etw" && OperatingSystem.IsWindows())
            {
                var captureId = item["captureId"]!.GetValue<string>();
                var captureManifest = Path.Combine(directory, captureId + ".manifest.json");
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(captureManifest, ct));
                var executable = manifest.RootElement.GetProperty("executable").GetString()!;
                var pid = root.TryGetProperty("targetProcessId", out var pidElement)
                    && pidElement.ValueKind == JsonValueKind.Number ? pidElement.GetInt32() : (int?)null;
                static DateTimeOffset? Timestamp(JsonElement record, string key) =>
                    record.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(value.GetString(), out var time) ? time : null;
                var analyzed = await new EtwTraceAnalyzer().AnalyzeAsync(raw, directory,
                    captureId, executable, pid,
                    Timestamp(root, "targetStartedAt"), Timestamp(root, "targetFinishedAt"), ct);
                item["report"] = Path.GetRelativePath(directory, analyzed.Summary);
                item["eventDump"] = Path.GetRelativePath(directory, analyzed.EventDump);
                item["samples"] = Path.GetRelativePath(directory, analyzed.Samples);
                item["targetProcessId"] = pid;
                var normalizedPath = Path.Combine(directory, "traces", "normalized", captureId + ".json");
                var normalized = File.Exists(normalizedPath)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(normalizedPath, ct))!.AsObject()
                    : new JsonObject();
                normalized["normalization"] = "target ETW event summary";
                normalized["summary"] = Path.GetRelativePath(directory, analyzed.Summary);
                normalized["samplesFile"] = Path.GetRelativePath(directory, analyzed.Samples);
                await File.WriteAllTextAsync(normalizedPath,
                    normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
            }
            else if (backend == "etw")
            {
                item["report"] = null;
                item["analysisStatus"] = "ETL conversion requires Windows";
            }
            else item["report"] = null;
            items.Add((JsonNode?)item);
        }
        var resultFile = Path.Combine(directory, "analysis.json");
        await File.WriteAllTextAsync(resultFile,
            items.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        await WriteTimingComparisonAsync(directory, ct);
        await WriteCounterComparisonAsync(directory, items, ct);
        Console.WriteLine($"Profile analysis: {resultFile}");
        return 0;
    }

    private static async Task WriteTimingComparisonAsync(string directory, CancellationToken ct)
    {
        var journal = Path.Combine(directory, "run.json");
        if (!File.Exists(journal)) return;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(journal, ct));
        var groups = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (node.GetProperty("kind").GetString() != "run"
                || node.GetProperty("status").GetString() != "succeeded"
                || !node.GetProperty("id").GetString()!.StartsWith("timing_", StringComparison.Ordinal)
                || !node.TryGetProperty("processDurationSeconds", out var duration)
                || duration.ValueKind != JsonValueKind.Number) continue;
            var id = node.GetProperty("id").GetString()!;
            var lastUnderscore = id.LastIndexOf('_');
            if (lastUnderscore <= "timing_".Length) continue;
            var name = id["timing_".Length..lastUnderscore];
            if (!groups.TryGetValue(name, out var values)) groups[name] = values = [];
            values.Add(duration.GetDouble());
        }
        if (groups.Count == 0) return;
        var baseline = groups.First();
        static double Median(IReadOnlyList<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length % 2 == 0
                ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
                : sorted[sorted.Length / 2];
        }
        var variants = new JsonArray();
        foreach (var group in groups)
        {
            var seconds = new JsonArray();
            foreach (var value in group.Value)
                seconds.Add((JsonNode?)JsonValue.Create(value, ProfileJson.Default.Double));
            variants.Add((JsonNode?)new JsonObject
            {
                ["name"] = group.Key, ["count"] = group.Value.Count,
                ["seconds"] = seconds,
                ["medianSeconds"] = JsonValue.Create(Median(group.Value), ProfileJson.Default.Double),
                ["ratioToBaseline"] = Median(baseline.Value) > 0
                    ? JsonValue.Create(Median(group.Value) / Median(baseline.Value), ProfileJson.Default.Double)
                    : null
            });
        }
        var summary = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["measurement"] = "uninstrumented process wall time in seconds",
            ["baseline"] = baseline.Key,
            ["variants"] = variants
        };
        var output = Path.Combine(directory, "comparison.json");
        await File.WriteAllTextAsync(output, summary.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = ProfileJson.Default }) + "\n", ct);
    }

    private sealed record Counter(double Value, string Unit);

    private static Dictionary<string, Counter> ReadPerfCounters(string path)
    {
        var counters = new Dictionary<string, Counter>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',');
            if (fields.Length < 3 || !double.TryParse(fields[0].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            var eventName = fields[2].Trim();
            if (eventName.Length == 0) continue;
            counters[eventName] = new Counter(value, fields[1].Trim());
        }
        return counters;
    }

    private static async Task WriteCounterComparisonAsync(string directory, JsonArray items, CancellationToken ct)
    {
        var variants = new Dictionary<string, Dictionary<string, Counter>>(StringComparer.Ordinal);
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["mode"]?.GetValue<string>() != "counters"
                || item["backend"]?.GetValue<string>() != "perf") continue;
            var id = item["captureId"]!.GetValue<string>();
            if (!id.StartsWith("counters_", StringComparison.Ordinal)) continue;
            var name = id["counters_".Length..];
            var raw = Path.Combine(directory, item["rawTrace"]!.GetValue<string>());
            variants[name] = ReadPerfCounters(raw);
        }
        if (variants.Count == 0) return;
        var orderedNames = ReadBuildVariantOrder(directory);
        var baselineName = orderedNames.FirstOrDefault(variants.ContainsKey)
            ?? variants.Keys.Order(StringComparer.Ordinal).First();
        var baseline = new KeyValuePair<string, Dictionary<string, Counter>>(
            baselineName, variants[baselineName]);
        var rows = new JsonArray();
        var displayOrder = orderedNames.Where(variants.ContainsKey)
            .Concat(variants.Keys.Except(orderedNames, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        foreach (var name in displayOrder)
        {
            var counters = variants[name];
            var events = new JsonObject();
            foreach (var (eventName, counter) in counters.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var compatible = baseline.Value.TryGetValue(eventName, out var reference)
                    && reference.Unit == counter.Unit && reference.Value > 0;
                events[eventName] = new JsonObject
                {
                    ["value"] = JsonValue.Create(counter.Value, ProfileJson.Default.Double),
                    ["unit"] = counter.Unit,
                    ["ratioToBaseline"] = compatible
                        ? JsonValue.Create(counter.Value / reference!.Value, ProfileJson.Default.Double) : null
                };
            }
            rows.Add((JsonNode?)new JsonObject { ["name"] = name, ["events"] = events });
        }
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["backend"] = "perf stat",
            ["baseline"] = baseline.Key,
            ["comparability"] = "Ratios require the same event, unit and measurement host; no cross-backend equivalence.",
            ["variants"] = rows
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "counter-comparison.json"),
            report.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, TypeInfoResolver = ProfileJson.Default }) + "\n", ct);
    }

    private static List<string> ReadBuildVariantOrder(string directory)
    {
        var path = Path.Combine(directory, "run.json");
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(x => x.GetProperty("kind").GetString() == "build")
            .Select(x => x.GetProperty("id").GetString()!)
            .Where(x => x.StartsWith("build_", StringComparison.Ordinal))
            .Select(x => x["build_".Length..]).ToList();
    }
}

[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(JsonNode))]
internal partial class ProfileJson : JsonSerializerContext;
