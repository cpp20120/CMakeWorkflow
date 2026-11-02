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
        var document = ProfileWorkflowBuilder.Build(
            variants.Select(v => new ProfileVariant(v.Name, v.BuildDirectory)).ToArray(),
            target, source, options.GetValueOrDefault("--config"), preset, repeats,
            seconds, targetArgs, options.GetValueOrDefault("--backend", "auto"),
            options.GetValueOrDefault("--mode", "cpu"));
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

}

public static class ProfileAnalyzer
{
    public static async Task<int> AnalyzeAsync(string directory, CancellationToken ct,
        bool emitResultPath = true)
    {
        directory = Path.GetFullPath(directory);
        var captures = Directory.GetFiles(directory, "*.results.json")
            .Order(StringComparer.Ordinal).ToArray();
        if (captures.Length == 0 && Directory.GetFiles(directory, "*.manifest.json").Length == 0)
            throw new FileNotFoundException("No profile capture results or manifests found.", directory);
        var items = new JsonArray();
        var analysisFailed = false;
        foreach (var capture in captures)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(capture, ct));
            var root = document.RootElement;
            var backend = root.GetProperty("backend").GetString()!;
            var mode = root.GetProperty("mode").GetString()!;
            var recordedStatus = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString() : null;
            if (recordedStatus is not null and not ("complete" or "succeeded"))
            {
                items.Add((JsonNode)new JsonObject
                {
                    ["captureId"] = root.GetProperty("captureId").GetString(),
                    ["backend"] = backend, ["mode"] = mode,
                    ["captureStatus"] = recordedStatus,
                    ["analysisStatus"] = "capture_failed"
                });
                analysisFailed = true;
                continue;
            }
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
                ["captureStatus"] = root.TryGetProperty("status", out var captureStatus)
                    ? captureStatus.GetString() : "unknown",
                ["crossPlatformMetricEquivalence"] = false
            };
            if (backend == "perf" && mode == "counters")
            {
                item["report"] = Path.GetRelativePath(directory, raw);
                var counters = new JsonObject();
                var counterWarnings = new List<string>();
                var rawCounters = ReadPerfCounters(raw, counterWarnings);
                foreach (var (eventName, counter) in rawCounters)
                    counters[eventName] = new JsonObject
                    {
                        ["value"] = JsonValue.Create(counter.Value, ProfileJson.Default.Double),
                        ["unit"] = counter.Unit,
                        ["domain"] = counter.Domain,
                        ["runningPercent"] = counter.RunningPercent is { } percent
                            ? JsonValue.Create(percent, ProfileJson.Default.Double) : null
                    };
                item["counters"] = counters;
                var derived = new JsonObject();
                foreach (var (metric, signal) in DeriveCounters(rawCounters))
                    derived[metric] = new JsonObject
                    {
                        ["value"] = JsonValue.Create(signal.Value, ProfileJson.Default.Double),
                        ["unit"] = signal.Unit, ["domain"] = signal.Domain,
                        ["quality"] = signal.RunningPercent is null ? "running-fraction-unknown"
                            : signal.RunningPercent < 100 ? "perf-scaled-multiplexed" : "reported-counts"
                    };
                item["derivedCounters"] = derived;
                if (rawCounters.Count > 0 && derived.Count == 0)
                    counterWarnings.Add("No compatible instructions/cycles event pair; derived PMU metrics unavailable.");
                item["analysisStatus"] = counters.Count == 0 ? "no_supported_counters"
                    : counterWarnings.Count > 0 ? "partial" : "complete";
                if (counterWarnings.Count > 0)
                {
                    var warnings = new JsonArray();
                    foreach (var warning in counterWarnings)
                        warnings.Add((JsonNode?)JsonValue.Create(warning));
                    item["analysisWarnings"] = warnings;
                }
            }
            else if (backend == "perf")
            {
                var report = Path.Combine(directory, item["captureId"]!.GetValue<string>() + ".perf-report.txt");
                try
                {
                    var result = await CommandExecution.RunAsync("perf",
                        ["report", "-i", raw, "--stdio", "--no-children", "--sort", "symbol,dso"],
                        directory, TimeSpan.FromMinutes(5), null, ct);
                    ProfileEnvironment.RequireSuccess(result, "perf report");
                    await ProfileArtifactWriter.WriteTextAsync(report, result.Output, ct);
                    item["report"] = Path.GetRelativePath(directory, report);
                    if (mode == "cpu")
                    {
                        var stacks = await PerfStackAnalyzer.AnalyzeAsync(raw, directory,
                            item["captureId"]!.GetValue<string>(), ct);
                        item["stacks"] = Path.GetRelativePath(directory, stacks);
                        var normalized = JsonNode.Parse(await File.ReadAllTextAsync(stacks, ct))!.AsObject();
                        var reportSamples = System.Text.RegularExpressions.Regex.Match(result.Output,
                            @"(?m)^#\s*Samples:\s*(\d+)");
                        var normalizedCount = normalized["samples"]!.GetValue<int>();
                        var parsedCount = normalized["parsedSamples"]!.GetValue<int>();
                        var status = normalized["analysisStatus"]!.GetValue<string>();
                        if (reportSamples.Success && int.TryParse(reportSamples.Groups[1].Value, out var reported))
                        {
                            item["reportedSamples"] = reported;
                            item["sampleCountMatchesReport"] = reported == normalizedCount;
                            if (reported > 0 && (normalizedCount == 0 || parsedCount == 0))
                                throw new InvalidDataException(
                                    $"perf report contains {reported} samples, but perf script decoded " +
                                    $"{normalizedCount} sample headers and {parsedCount} nonempty stacks.");
                            if (reported != normalizedCount)
                            {
                                // The two perf interfaces can expose different subsets. Do not
                                // silently claim complete analysis when the counts disagree.
                                status = "partial";
                                item["analysisWarnings"] = new JsonArray(
                                    JsonValue.Create($"perf report counts {reported} samples, " +
                                        $"but perf script parsed {normalizedCount}; verify event/field selection."));
                            }
                        }
                        if (status == "partial")
                        {
                            var warnings = item["analysisWarnings"] as JsonArray ?? new JsonArray();
                            foreach (var (field, description) in new[]
                            {
                                ("rejectedRecords", "unrecognized perf script lines"),
                                ("truncatedFrames", "discarded stack frames"),
                                ("emptyStackSamples", "samples with no stack frames"),
                                ("droppedStackSamples", "samples dropped by distinct-stack limit")
                            })
                            {
                                var count = normalized[field]?.GetValue<int>() ?? 0;
                                if (count > 0)
                                    warnings.Add((JsonNode?)JsonValue.Create($"{count} {description}; check normalized stack quality."));
                            }
                            if (warnings.Count > 0) item["analysisWarnings"] = warnings;
                        }
                        item["analysisStatus"] = status;
                        var hotFunctions = new JsonArray();
                        foreach (var function in normalized["hotFunctions"]!.AsArray().Take(5))
                            hotFunctions.Add((JsonNode?)function?.DeepClone());
                        item["summary"] = new JsonObject
                        {
                            ["samples"] = normalized["samples"]!.DeepClone(),
                            ["parsedSamples"] = normalized["parsedSamples"]!.DeepClone(),
                            ["symbolizedSamples"] = normalized["symbolizedSamples"]!.DeepClone(),
                            ["fullyResolvedSamples"] = normalized["fullyResolvedSamples"]!.DeepClone(),
                            ["resolvedFrames"] = normalized["resolvedFrames"]!.DeepClone(),
                            ["unresolvedFrames"] = normalized["unresolvedFrames"]!.DeepClone(),
                            ["hotFunctions"] = hotFunctions
                        };
                    }
                    else if (mode == "scheduler")
                    {
                        var timeline = await PerfSchedulerAnalyzer.AnalyzeAsync(raw, directory,
                            item["captureId"]!.GetValue<string>(), ct);
                        item["timeline"] = Path.GetRelativePath(directory, timeline);
                        item["analysisStatus"] = "complete";
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Preserve raw perf.data when stack/scheduler decoding is
                    // unsupported by the perf version or event permissions.
                    item["analysisStatus"] = "failed";
                    item["analysisError"] = error.Message;
                    analysisFailed = true;
                }
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
                item["stacks"] = Path.GetRelativePath(directory,
                    Path.Combine("traces", "normalized", captureId + ".stacks.json"));
                item["timeline"] = Path.GetRelativePath(directory,
                    Path.Combine("traces", "normalized", captureId + ".timeline.jsonl"));
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
        // A failed or interrupted capture has a manifest and a runs.jsonl
        // record, but intentionally has no *.results.json. Iterating only
        // successful captures would falsely describe such a run as complete.
        var captureJournal = ReadCaptureJournal(directory);
        foreach (var manifestPath in Directory.GetFiles(directory, "*.manifest.json")
            .Order(StringComparer.Ordinal))
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
            var manifestRoot = manifest.RootElement;
            // Use the observed basename for all local paths; never let an
            // untrusted JSON field construct a relative result path.
            const string suffix = ".manifest.json";
            var fileName = Path.GetFileName(manifestPath);
            var captureId = fileName[..^suffix.Length];
            if (!manifestRoot.TryGetProperty("captureId", out var captureIdElement)) continue;
            if (captureIdElement.ValueKind != JsonValueKind.String
                || captureIdElement.GetString() != captureId)
            {
                items.Add((JsonNode?)new JsonObject
                {
                    ["captureId"] = captureId,
                    ["analysisStatus"] = "capture_incomplete",
                    ["analysisError"] = "Capture manifest identity does not match its filename."
                });
                analysisFailed = true;
                continue;
            }
            if (File.Exists(Path.Combine(directory, captureId + ".results.json"))) continue;
            captureJournal.TryGetValue(captureId, out var failure);
            items.Add((JsonNode?)new JsonObject
            {
                ["captureId"] = captureId,
                ["backend"] = manifestRoot.TryGetProperty("backend", out var backendValue)
                    ? backendValue.GetString() : null,
                ["mode"] = manifestRoot.TryGetProperty("mode", out var modeValue)
                    ? modeValue.GetString() : null,
                ["captureStatus"] = failure.Status ?? "missing_result",
                ["analysisStatus"] = "capture_incomplete",
                ["analysisError"] = failure.Error ?? "Capture has a manifest but no committed result."
            });
            analysisFailed = true;
        }
        var resultFile = Path.Combine(directory, "analysis.json");
        await WriteTimingComparisonAsync(directory, ct);
        await WriteCounterComparisonAsync(directory, items, ct);
        await ProfileArtifactWriter.WriteTextAsync(resultFile,
            items.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        if (emitResultPath) Console.WriteLine($"Profile analysis: {resultFile}");
        return analysisFailed ? 1 : 0;
    }

    private static Dictionary<string, (string? Status, string? Error)> ReadCaptureJournal(string directory)
    {
        var journal = new Dictionary<string, (string? Status, string? Error)>(StringComparer.Ordinal);
        var path = Path.Combine(directory, "runs.jsonl");
        if (!File.Exists(path)) return journal;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                if (!root.TryGetProperty("captureId", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("status", out var status)
                    || status.ValueKind != JsonValueKind.String) continue;
                var error = root.TryGetProperty("error", out var message)
                    && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
                journal[id.GetString()!] = (status.GetString(), error);
            }
            catch (JsonException)
            {
                // A torn journal line cannot authorize a successful capture.
                // Manifest-without-result detection below still fails closed.
            }
        }
        return journal;
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
        await ProfileArtifactWriter.WriteTextAsync(output, summary.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = ProfileJson.Default }) + "\n", ct);
    }

    private sealed record Counter(double Value, string Unit, string Domain,
        double? RunningPercent);

    private static string EventDomain(string name) => name.EndsWith(":u", StringComparison.Ordinal)
        ? "user" : name.EndsWith(":k", StringComparison.Ordinal) ? "kernel"
        : name.Contains(':') ? "other" : "unspecified";

    private static Dictionary<string, Counter> ReadPerfCounters(string path,
        List<string>? warnings = null)
    {
        var counters = new Dictionary<string, Counter>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',');
            if (fields.Length < 3 || !double.TryParse(fields[0].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < 0)
            {
                if (warnings is { Count: < 32 })
                    warnings.Add($"Line {lineNumber}: unsupported or invalid perf stat measurement.");
                continue;
            }
            var eventName = fields[2].Trim();
            if (eventName.Length == 0)
            {
                if (warnings is { Count: < 32 }) warnings.Add($"Line {lineNumber}: missing event name.");
                continue;
            }
            double? runningPercent = fields.Length > 4 && double.TryParse(fields[4].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                && double.IsFinite(percent) && percent is >= 0 and <= 100 ? percent : null;
            counters[eventName] = new Counter(value, fields[1].Trim(),
                EventDomain(eventName), runningPercent);
        }
        return counters;
    }

    // Normalized, dimensionless PMU signals. Missing or unsupported counters
    // remain absent; zero is never substituted for missing PMU data.
    private static Dictionary<string, Counter> DeriveCounters(Dictionary<string, Counter> raw)
    {
        var result = new Dictionary<string, Counter>(StringComparer.Ordinal);
        static double? MinimumRunning(Counter a, Counter b) =>
            a.RunningPercent is { } x && b.RunningPercent is { } y ? Math.Min(x, y) : null;
        // A ratio may use only counters captured in the same event domain.
        // perf stat prints already scaled values for multiplexed events; retain
        // that qualification in the result rather than treating a missing
        // or unsupported event as zero.
        var domains = new[] { (Suffix: "", Name: "unspecified"),
            (Suffix: ":u", Name: "user"), (Suffix: ":k", Name: "kernel") };
        var matching = domains.Where(d => raw.ContainsKey("instructions" + d.Suffix)
            && raw.ContainsKey("cycles" + d.Suffix)).ToArray();
        if (matching.Length != 1) return result;
        var domain = matching[0];
        var instructions = raw["instructions" + domain.Suffix];
        var cycles = raw["cycles" + domain.Suffix];
        if (cycles.Value > 0 && instructions.Value > 0)
        {
            result["ipc"] = new Counter(instructions.Value / cycles.Value,
                "instructions/cycle", domain.Name, MinimumRunning(instructions, cycles));
        }
        if (instructions.Value <= 0) return result;
        if (raw.TryGetValue("cache-misses" + domain.Suffix, out var misses))
            result["cache_mpki"] = new Counter(1000.0 * misses.Value / instructions.Value,
                "misses/kinstructions", domain.Name, MinimumRunning(instructions, misses));
        if (raw.TryGetValue("branch-misses" + domain.Suffix, out var branchMisses))
            result["branch_mpki"] = new Counter(1000.0 * branchMisses.Value / instructions.Value,
                "misses/kinstructions", domain.Name, MinimumRunning(instructions, branchMisses));
        return result;
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
            ?? (variants.ContainsKey("before") ? "before" : variants.Keys.Order(StringComparer.Ordinal).First());
        var baseline = new KeyValuePair<string, Dictionary<string, Counter>>(
            baselineName, variants[baselineName]);
        var rows = new JsonArray();
        var displayOrder = orderedNames.Where(variants.ContainsKey)
            .Concat(variants.Keys.Except(orderedNames, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        var baselineDerived = DeriveCounters(baseline.Value);
        foreach (var name in displayOrder)
        {
            var counters = variants[name];
            var events = new JsonObject();
            foreach (var (eventName, counter) in counters.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var compatible = baseline.Value.TryGetValue(eventName, out var reference)
                    && reference.Unit == counter.Unit && reference.Value > 0
                    && reference.RunningPercent == 100 && counter.RunningPercent == 100;
                events[eventName] = new JsonObject
                {
                    ["value"] = JsonValue.Create(counter.Value, ProfileJson.Default.Double),
                    ["unit"] = counter.Unit,
                    ["domain"] = counter.Domain,
                    ["runningPercent"] = counter.RunningPercent is { } percent
                        ? JsonValue.Create(percent, ProfileJson.Default.Double) : null,
                    ["ratioToBaseline"] = compatible
                        ? JsonValue.Create(counter.Value / reference!.Value, ProfileJson.Default.Double) : null
                };
            }
            var derived = new JsonObject();
            foreach (var (metric, signal) in DeriveCounters(counters))
            {
                var reference = baselineDerived.GetValueOrDefault(metric);
                var ratio = reference is not null && reference.Value > 0
                    && reference.Unit == signal.Unit && reference.Domain == signal.Domain
                    && reference.RunningPercent == 100 && signal.RunningPercent == 100
                    ? signal.Value / reference.Value : (double?)null;
                derived[metric] = new JsonObject
                {
                    ["value"] = JsonValue.Create(signal.Value, ProfileJson.Default.Double),
                    ["unit"] = signal.Unit,
                    ["domain"] = signal.Domain,
                    ["quality"] = signal.RunningPercent is null ? "running-fraction-unknown"
                        : signal.RunningPercent < 100 ? "perf-scaled-multiplexed" : "reported-counts",
                    ["ratioToBaseline"] = ratio is null ? null
                        : JsonValue.Create(ratio.Value, ProfileJson.Default.Double)
                };
            }
            rows.Add((JsonNode?)new JsonObject { ["name"] = name, ["events"] = events,
                ["derived"] = derived });
        }
        var report = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["backend"] = "perf stat",
            ["baseline"] = baseline.Key,
            ["comparability"] = "Derived metrics require one common event domain. Cross-run ratios require matching domain and non-multiplexed counters; no cross-backend equivalence.",
            ["variants"] = rows
        };
        await ProfileArtifactWriter.WriteTextAsync(Path.Combine(directory, "counter-comparison.json"),
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
