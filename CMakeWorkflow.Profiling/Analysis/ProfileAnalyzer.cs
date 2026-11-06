using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Capture;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Analysis;
#pragma warning restore IDE0130

public static class ProfileAnalyzer
{
    public static async Task<int> AnalyzeAsync(string directory, CancellationToken ct,
        bool emitResultPath = true)
    {
        directory = Path.GetFullPath(directory);
        string[] captures = [.. Directory.GetFiles(directory, "*.results.json").Order(StringComparer.Ordinal)];
        if (captures.Length == 0 && Directory.GetFiles(directory, "*.manifest.json").Length == 0)
        {
            throw new FileNotFoundException("No profile capture results or manifests found.", directory);
        }

        JsonArray items = [];
        bool analysisFailed = false;
        foreach (string? capture in captures)
        {
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(capture, ct));
            JsonElement root = document.RootElement;
            string backend = root.GetProperty("backend").GetString()!;
            string mode = root.GetProperty("mode").GetString()!;
            string? recordedStatus = root.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString()
                : null;
            if (recordedStatus is not null and not ("complete" or "succeeded"))
            {
                items.Add((JsonNode)new JsonObject
                {
                    ["captureId"] = root.GetProperty("captureId").GetString(),
                    ["backend"] = backend,
                    ["mode"] = mode,
                    ["captureStatus"] = recordedStatus,
                    ["analysisStatus"] = "capture_failed"
                });
                analysisFailed = true;
                continue;
            }

            string raw = Path.GetFullPath(Path.Combine(directory, root.GetProperty("rawTrace").GetString()!));
            if (!raw.StartsWith(directory + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new IOException($"Profile trace path escapes the run directory: {raw}");
            }

            if (!File.Exists(raw) && !Directory.Exists(raw))
            {
                throw new FileNotFoundException("Profile trace is missing.", raw);
            }

            if (root.TryGetProperty("rawTraceSha256", out JsonElement expectedHash)
                && await ProfileArtifactHash.Sha256Async(raw, ct) != expectedHash.GetString())
            {
                throw new IOException($"Profile trace changed after capture: {raw}");
            }

            JsonObject item = new()
            {
                ["captureId"] = root.GetProperty("captureId").GetString(),
                ["backend"] = backend,
                ["mode"] = mode,
                ["rawTrace"] = Path.GetRelativePath(directory, raw),
                ["eventSource"] = root.GetProperty("eventSource").GetString(),
                ["captureStatus"] = root.TryGetProperty("status", out JsonElement captureStatus)
                    ? captureStatus.GetString()
                    : "unknown",
                ["crossPlatformMetricEquivalence"] = false
            };
            if (backend == "perf" && mode == "counters")
            {
                item["report"] = Path.GetRelativePath(directory, raw);
                JsonObject counters = [];
                List<string> counterWarnings = [];
                Dictionary<string, Counter> rawCounters = ReadPerfCounters(raw, counterWarnings);
                foreach ((string? eventName, Counter? counter) in rawCounters)
                {
                    counters[eventName] = new JsonObject
                    {
                        ["value"] = JsonValue.Create(counter.Value, ProfileJson.Default.Double),
                        ["unit"] = counter.Unit,
                        ["domain"] = counter.Domain,
                        ["runningPercent"] = counter.RunningPercent is { } percent
                            ? JsonValue.Create(percent, ProfileJson.Default.Double)
                            : null
                    };
                }

                item["counters"] = counters;
                JsonObject derived = [];
                foreach ((string? metric, Counter? signal) in DeriveCounters(rawCounters))
                {
                    derived[metric] = new JsonObject
                    {
                        ["value"] = JsonValue.Create(signal.Value, ProfileJson.Default.Double),
                        ["unit"] = signal.Unit,
                        ["domain"] = signal.Domain,
                        ["quality"] = signal.RunningPercent is null ? "running-fraction-unknown"
                            : signal.RunningPercent < 100 ? "perf-scaled-multiplexed" : "reported-counts"
                    };
                }

                item["derivedCounters"] = derived;
                if (rawCounters.Count > 0 && derived.Count == 0)
                {
                    counterWarnings.Add(
                        "No compatible instructions/cycles event pair; derived PMU metrics unavailable.");
                }

                item["analysisStatus"] = counters.Count == 0 ? "no_supported_counters"
                    : counterWarnings.Count > 0 ? "partial" : "complete";
                if (counterWarnings.Count > 0)
                {
                    JsonArray warnings = [];
                    foreach (string warning in counterWarnings)
                    {
                        warnings.Add((JsonNode?)JsonValue.Create(warning));
                    }

                    item["analysisWarnings"] = warnings;
                }
            }
            else if (backend == "perf")
            {
                string report = Path.Combine(directory, item["captureId"]!.GetValue<string>() + ".perf-report.txt");
                try
                {
                    CommandResult result = await CommandExecution.RunAsync("perf",
                        ["report", "-i", raw, "--stdio", "--no-children", "--sort", "symbol,dso"],
                        directory, TimeSpan.FromMinutes(5), null, ct);
                    ProfileEnvironment.RequireSuccess(result, "perf report");
                    await ProfileArtifactWriter.WriteTextAsync(report, result.Output, ct);
                    item["report"] = Path.GetRelativePath(directory, report);
                    if (mode == "cpu")
                    {
                        string stacks = await PerfStackAnalyzer.AnalyzeAsync(raw, directory,
                            item["captureId"]!.GetValue<string>(), ct);
                        item["stacks"] = Path.GetRelativePath(directory, stacks);
                        JsonObject normalized = JsonNode.Parse(await File.ReadAllTextAsync(stacks, ct))!.AsObject();
                        Match reportSamples = Regex.Match(result.Output,
                            @"(?m)^#\s*Samples:\s*(\d+)");
                        int normalizedCount = normalized["samples"]!.GetValue<int>();
                        int parsedCount = normalized["parsedSamples"]!.GetValue<int>();
                        string status = normalized["analysisStatus"]!.GetValue<string>();
                        if (reportSamples.Success && int.TryParse(reportSamples.Groups[1].Value, out int reported))
                        {
                            item["reportedSamples"] = reported;
                            item["sampleCountMatchesReport"] = reported == normalizedCount;
                            if (reported > 0 && (normalizedCount == 0 || parsedCount == 0))
                            {
                                throw new InvalidDataException(
                                    $"perf report contains {reported} samples, but perf script decoded " +
                                    $"{normalizedCount} sample headers and {parsedCount} nonempty stacks.");
                            }

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
                            JsonArray warnings = item["analysisWarnings"] as JsonArray ?? [];
                            foreach ((string? field, string? description) in new[]
                                     {
                                         ("rejectedRecords", "unrecognized perf script lines"),
                                         ("truncatedFrames", "discarded stack frames"),
                                         ("emptyStackSamples", "samples with no stack frames"),
                                         ("droppedStackSamples", "samples dropped by distinct-stack limit")
                                     })
                            {
                                int count = normalized[field]?.GetValue<int>() ?? 0;
                                if (count > 0)
                                {
                                    warnings.Add(
                                        (JsonNode?)JsonValue.Create(
                                            $"{count} {description}; check normalized stack quality."));
                                }
                            }

                            if (warnings.Count > 0)
                            {
                                item["analysisWarnings"] = warnings;
                            }
                        }

                        item["analysisStatus"] = status;
                        JsonArray hotFunctions = [];
                        foreach (JsonNode? function in normalized["hotFunctions"]!.AsArray().Take(5))
                        {
                            hotFunctions.Add(function?.DeepClone());
                        }

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
                        string timeline = await PerfSchedulerAnalyzer.AnalyzeAsync(raw, directory,
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
                string report = Path.Combine(directory, item["captureId"]!.GetValue<string>() + ".toc.xml");
                CommandResult result = await CommandExecution.RunAsync("xcrun",
                    ["xctrace", "export", raw, "--toc", "--output", report],
                    directory, TimeSpan.FromMinutes(5), null, ct);
                ProfileEnvironment.RequireSuccess(result, "xctrace export");
                item["report"] = Path.GetRelativePath(directory, report);
            }
            else if (backend == "etw" && OperatingSystem.IsWindows())
            {
                string captureId = item["captureId"]!.GetValue<string>();
                string captureManifest = Path.Combine(directory, captureId + ".manifest.json");
                using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(captureManifest, ct));
                string executable = manifest.RootElement.GetProperty("executable").GetString()!;
                int? pid = root.TryGetProperty("targetProcessId", out JsonElement pidElement)
                          && pidElement.ValueKind == JsonValueKind.Number
                    ? pidElement.GetInt32()
                    : null;

                static DateTimeOffset? Timestamp(JsonElement record, string key) =>
                    record.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                                                              && DateTimeOffset.TryParse(value.GetString(),
                                                                  out DateTimeOffset time)
                        ? time
                        : null;

                EtwAnalysisResult analyzed = await new EtwTraceAnalyzer().AnalyzeAsync(raw, directory,
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
                string normalizedPath = Path.Combine(directory, "traces", "normalized", captureId + ".json");
                JsonObject normalized = File.Exists(normalizedPath)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(normalizedPath, ct))!.AsObject()
                    : [];
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
            else
            {
                item["report"] = null;
            }

            items.Add((JsonNode?)item);
        }

        // A failed or interrupted capture has a manifest and a runs.jsonl
        // record, but intentionally has no *.results.json. Iterating only
        // successful captures would falsely describe such a run as complete.
        Dictionary<string, (string? Status, string? Error)> captureJournal = ReadCaptureJournal(directory);
        foreach (string? manifestPath in Directory.GetFiles(directory, "*.manifest.json")
                     .Order(StringComparer.Ordinal))
        {
            using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
            JsonElement manifestRoot = manifest.RootElement;
            // Use the observed basename for all local paths; never let an
            // untrusted JSON field construct a relative result path.
            const string suffix = ".manifest.json";
            string fileName = Path.GetFileName(manifestPath);
            string captureId = fileName[..^suffix.Length];
            if (!manifestRoot.TryGetProperty("captureId", out JsonElement captureIdElement))
            {
                continue;
            }

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

            if (File.Exists(Path.Combine(directory, captureId + ".results.json")))
            {
                continue;
            }

            _ = captureJournal.TryGetValue(captureId, out (string? Status, string? Error) failure);
            items.Add((JsonNode?)new JsonObject
            {
                ["captureId"] = captureId,
                ["backend"] = manifestRoot.TryGetProperty("backend", out JsonElement backendValue)
                    ? backendValue.GetString()
                    : null,
                ["mode"] = manifestRoot.TryGetProperty("mode", out JsonElement modeValue)
                    ? modeValue.GetString()
                    : null,
                ["captureStatus"] = failure.Status ?? "missing_result",
                ["analysisStatus"] = "capture_incomplete",
                ["analysisError"] = failure.Error ?? "Capture has a manifest but no committed result."
            });
            analysisFailed = true;
        }

        string resultFile = Path.Combine(directory, "analysis.json");
        await WriteTimingComparisonAsync(directory, ct);
        await WriteCounterComparisonAsync(directory, items, ct);
        await ProfileArtifactWriter.WriteTextAsync(resultFile,
            items.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        if (emitResultPath)
        {
            Console.WriteLine($"Profile analysis: {resultFile}");
        }

        return analysisFailed ? 1 : 0;
    }

    private static Dictionary<string, (string? Status, string? Error)> ReadCaptureJournal(string directory)
    {
        Dictionary<string, (string? Status, string? Error)> journal = new(StringComparer.Ordinal);
        string path = Path.Combine(directory, "runs.jsonl");
        if (!File.Exists(path))
        {
            return journal;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument record = JsonDocument.Parse(line);
                JsonElement root = record.RootElement;
                if (!root.TryGetProperty("captureId", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("status", out JsonElement status)
                    || status.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? error = root.TryGetProperty("error", out JsonElement message)
                            && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
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
        string journal = Path.Combine(directory, "run.json");
        if (!File.Exists(journal))
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(journal, ct));
        Dictionary<string, List<double>> groups = new(StringComparer.Ordinal);
        foreach (JsonElement node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (node.GetProperty("kind").GetString() != "run"
                || node.GetProperty("status").GetString() != "succeeded"
                || !node.GetProperty("id").GetString()!.StartsWith("timing_", StringComparison.Ordinal)
                || !node.TryGetProperty("processDurationSeconds", out JsonElement duration)
                || duration.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            string id = node.GetProperty("id").GetString()!;
            int lastUnderscore = id.LastIndexOf('_');
            if (lastUnderscore <= "timing_".Length)
            {
                continue;
            }

            string name = id["timing_".Length..lastUnderscore];
            if (!groups.TryGetValue(name, out List<double>? values))
            {
                groups[name] = values = [];
            }

            values.Add(duration.GetDouble());
        }

        if (groups.Count == 0)
        {
            return;
        }

        KeyValuePair<string, List<double>> baseline = groups.First();

        static double Median(IReadOnlyList<double> values)
        {
            double[] sorted = [.. values.Order()];
            return sorted.Length % 2 == 0
                ? (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2
                : sorted[sorted.Length / 2];
        }

        JsonArray variants = [];
        foreach (KeyValuePair<string, List<double>> group in groups)
        {
            JsonArray seconds = [];
            foreach (double value in group.Value)
            {
                seconds.Add((JsonNode?)JsonValue.Create(value, ProfileJson.Default.Double));
            }

            variants.Add((JsonNode?)new JsonObject
            {
                ["name"] = group.Key,
                ["count"] = group.Value.Count,
                ["seconds"] = seconds,
                ["medianSeconds"] = JsonValue.Create(Median(group.Value), ProfileJson.Default.Double),
                ["ratioToBaseline"] = Median(baseline.Value) > 0
                    ? JsonValue.Create(Median(group.Value) / Median(baseline.Value), ProfileJson.Default.Double)
                    : null
            });
        }

        JsonObject summary = new()
        {
            ["schemaVersion"] = 1,
            ["measurement"] = "uninstrumented process wall time in seconds",
            ["baseline"] = baseline.Key,
            ["variants"] = variants
        };
        string output = Path.Combine(directory, "comparison.json");
        await ProfileArtifactWriter.WriteTextAsync(output, summary.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = ProfileJson.Default }) + "\n", ct);
    }

    private sealed record Counter(
        double Value,
        string Unit,
        string Domain,
        double? RunningPercent);

    private static string EventDomain(string name) => name.EndsWith(":u", StringComparison.Ordinal)
        ? "user"
        : name.EndsWith(":k", StringComparison.Ordinal)
            ? "kernel"
            : name.Contains(':', StringComparison.Ordinal)
                ? "other"
                : "unspecified";

    private static Dictionary<string, Counter> ReadPerfCounters(string path,
        List<string>? warnings = null)
    {
        Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] fields = line.Split(',');
            if (fields.Length < 3 || !double.TryParse(fields[0].Trim(),
                                      NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                                  || !double.IsFinite(value) || value < 0)
            {
                if (warnings is { Count: < 32 })
                {
                    warnings.Add($"Line {lineNumber}: unsupported or invalid perf stat measurement.");
                }

                continue;
            }

            string eventName = fields[2].Trim();
            if (eventName.Length == 0)
            {
                if (warnings is { Count: < 32 })
                {
                    warnings.Add($"Line {lineNumber}: missing event name.");
                }

                continue;
            }

            double? runningPercent = fields.Length > 4 && double.TryParse(fields[4].Trim(),
                                                           NumberStyles.Float, CultureInfo.InvariantCulture,
                                                           out double percent)
                                                       && double.IsFinite(percent) && percent is >= 0 and <= 100
                ? percent
                : null;
            counters[eventName] = new Counter(value, fields[1].Trim(),
                EventDomain(eventName), runningPercent);
        }

        return counters;
    }

    // Normalized, dimensionless PMU signals. Missing or unsupported counters
    // remain absent; zero is never substituted for missing PMU data.
    private static Dictionary<string, Counter> DeriveCounters(Dictionary<string, Counter> raw)
    {
        Dictionary<string, Counter> result = new(StringComparer.Ordinal);

        static double? MinimumRunning(Counter a, Counter b) =>
            a.RunningPercent is { } x && b.RunningPercent is { } y ? Math.Min(x, y) : null;

        // A ratio may use only counters captured in the same event domain.
        // perf stat prints already scaled values for multiplexed events; retain
        // that qualification in the result rather than treating a missing
        // or unsupported event as zero.
        (string Suffix, string Name)[] domains =
        [
            (Suffix: "", Name: "unspecified"),
            (Suffix: ":u", Name: "user"), (Suffix: ":k", Name: "kernel")
        ];
        (string Suffix, string Name)[] matching = [.. domains.Where(d => raw.ContainsKey("instructions" + d.Suffix)
                                          && raw.ContainsKey("cycles" + d.Suffix))];
        if (matching.Length != 1)
        {
            return result;
        }

        (string? Suffix, string? Name) = matching[0];
        Counter instructions = raw["instructions" + Suffix];
        Counter cycles = raw["cycles" + Suffix];
        if (cycles.Value > 0 && instructions.Value > 0)
        {
            result["ipc"] = new Counter(instructions.Value / cycles.Value,
                "instructions/cycle", Name, MinimumRunning(instructions, cycles));
        }

        if (instructions.Value <= 0)
        {
            return result;
        }

        if (raw.TryGetValue("cache-misses" + Suffix, out Counter? misses))
        {
            result["cache_mpki"] = new Counter(1000.0 * misses.Value / instructions.Value,
                "misses/kinstructions", Name, MinimumRunning(instructions, misses));
        }

        if (raw.TryGetValue("branch-misses" + Suffix, out Counter? branchMisses))
        {
            result["branch_mpki"] = new Counter(1000.0 * branchMisses.Value / instructions.Value,
                "misses/kinstructions", Name, MinimumRunning(instructions, branchMisses));
        }

        return result;
    }

    private static async Task WriteCounterComparisonAsync(string directory, JsonArray items, CancellationToken ct)
    {
        Dictionary<string, Dictionary<string, Counter>> variants = new(StringComparer.Ordinal);
        foreach (JsonObject item in items.OfType<JsonObject>())
        {
            if (item["mode"]?.GetValue<string>() != "counters"
                || item["backend"]?.GetValue<string>() != "perf")
            {
                continue;
            }

            string id = item["captureId"]!.GetValue<string>();
            if (!id.StartsWith("counters_", StringComparison.Ordinal))
            {
                continue;
            }

            string name = id["counters_".Length..];
            string raw = Path.Combine(directory, item["rawTrace"]!.GetValue<string>());
            variants[name] = ReadPerfCounters(raw);
        }

        if (variants.Count == 0)
        {
            return;
        }

        List<string> orderedNames = ReadBuildVariantOrder(directory);
        string baselineName = orderedNames.FirstOrDefault(variants.ContainsKey)
                           ?? (variants.ContainsKey("before")
                               ? "before"
                               : variants.Keys.Order(StringComparer.Ordinal).First());
        KeyValuePair<string, Dictionary<string, Counter>> baseline = new(
            baselineName, variants[baselineName]);
        JsonArray rows = [];
        IEnumerable<string> displayOrder = orderedNames.Where(variants.ContainsKey)
            .Concat(variants.Keys.Except(orderedNames, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        Dictionary<string, Counter> baselineDerived = DeriveCounters(baseline.Value);
        foreach (string? name in displayOrder)
        {
            Dictionary<string, Counter> counters = variants[name];
            JsonObject events = [];
            foreach ((string? eventName, Counter? counter) in counters.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                bool compatible = baseline.Value.TryGetValue(eventName, out Counter? reference)
                                 && reference.Unit == counter.Unit && reference.Value > 0
                                 && reference.RunningPercent == 100 && counter.RunningPercent == 100;
                events[eventName] = new JsonObject
                {
                    ["value"] = JsonValue.Create(counter.Value, ProfileJson.Default.Double),
                    ["unit"] = counter.Unit,
                    ["domain"] = counter.Domain,
                    ["runningPercent"] = counter.RunningPercent is { } percent
                        ? JsonValue.Create(percent, ProfileJson.Default.Double)
                        : null,
                    ["ratioToBaseline"] = compatible
                        ? JsonValue.Create(counter.Value / reference!.Value, ProfileJson.Default.Double)
                        : null
                };
            }

            JsonObject derived = [];
            foreach ((string? metric, Counter? signal) in DeriveCounters(counters))
            {
                Counter? reference = baselineDerived.GetValueOrDefault(metric);
                double? ratio = reference is not null && reference.Value > 0
                                                  && reference.Unit == signal.Unit && reference.Domain == signal.Domain
                                                  && reference.RunningPercent == 100 && signal.RunningPercent == 100
                    ? signal.Value / reference.Value
                    : null;
                derived[metric] = new JsonObject
                {
                    ["value"] = JsonValue.Create(signal.Value, ProfileJson.Default.Double),
                    ["unit"] = signal.Unit,
                    ["domain"] = signal.Domain,
                    ["quality"] = signal.RunningPercent is null ? "running-fraction-unknown"
                        : signal.RunningPercent < 100 ? "perf-scaled-multiplexed" : "reported-counts",
                    ["ratioToBaseline"] = ratio is null
                        ? null
                        : JsonValue.Create(ratio.Value, ProfileJson.Default.Double)
                };
            }

            rows.Add((JsonNode?)new JsonObject
            {
                ["name"] = name,
                ["events"] = events,
                ["derived"] = derived
            });
        }

        JsonObject report = new()
        {
            ["schemaVersion"] = 2,
            ["backend"] = "perf stat",
            ["baseline"] = baseline.Key,
            ["comparability"] =
                "Derived metrics require one common event domain. Cross-run ratios require matching domain and non-multiplexed counters; no cross-backend equivalence.",
            ["variants"] = rows
        };
        await ProfileArtifactWriter.WriteTextAsync(Path.Combine(directory, "counter-comparison.json"),
            report.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, TypeInfoResolver = ProfileJson.Default }) + "\n", ct);
    }

    private static List<string> ReadBuildVariantOrder(string directory)
    {
        string path = Path.Combine(directory, "run.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(x => x.GetProperty("kind").GetString() == "build")
            .Select(x => x.GetProperty("id").GetString()!)
            .Where(x => x.StartsWith("build_", StringComparison.Ordinal))
            .Select(x => x["build_".Length..])];
    }
}
