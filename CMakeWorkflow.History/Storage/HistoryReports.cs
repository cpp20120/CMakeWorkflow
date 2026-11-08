using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History.Storage;

/// <summary>Reads completed result formats into detached history import evidence without database mutation.</summary>
/// <remarks>
/// Workflow reports, campaign completion markers, fuzz reports and standalone captures have different
/// completion rules. Reading historical results is not durable Host recovery authorization. Paths stored
/// in reports are resolved as artifact metadata, not inherently workspace-authorized file handles; callers
/// apply their own path policy. Parse errors propagate so the store can reject a batch before writing it.
/// </remarks>
internal static class HistoryReports
{
    private static string? Str(JsonElement e, string name)
    {
        return !e.TryGetProperty(name, out JsonElement value) || value.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined
            ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                                              && value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Json(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetRawText()
            : null;


    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));


    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(stream));

    }

    private static string? Time(JsonElement e, string name)
    {
        string? value = Str(e, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset time)
            ? time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : null;
    }

    private static string Id(string directory) => Hash(Encoding.UTF8.GetBytes(directory));
    private static string Canonical(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static string? RelativeArtifact(string directory, string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? null
            : Path.GetFullPath(Path.IsPathFullyQualified(name) ? name : Path.Combine(directory, name));
    }

    private static void AddArtifact(ParsedRun run, string? node, string type, string? path,
        string? hash = null, string? metadata = null)
    {
        string? full = RelativeArtifact(run.Run.Directory, path);
        if (full is not null)
        {
            run.Artifacts.Add(new(run.Run.Id, node, type, full, hash, metadata));
        }
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllBytes(path));

    private static void Version(JsonElement root, string path, int expected, bool required = true)
    {
        if (!root.TryGetProperty("schemaVersion", out JsonElement v))
        {
            if (required)
            {
                throw new InvalidDataException($"{path}: missing schemaVersion; expected {expected}.");
            }

            return;
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n != expected)
        {
            throw new InvalidDataException($"{path}: unsupported schemaVersion {v}; expected {expected}.");
        }
    }

    /// <summary>Selects one results format and enriches its completed evidence from files in the directory.</summary>
    /// <remarks>
    /// Precedence is run.json, campaign-completion.json, report.json, then top-level capture results.
    /// An incomplete selected format returns null where its parser permits; it does not fall through to another
    /// format. Unsupported/corrupt evidence can throw. No filesystem lease or stable multi-file snapshot is acquired.
    /// </remarks>
    /// <param name="directory">Directory normalized by full path and trailing-separator trimming; not symlink canonicalization.</param>
    /// <returns>Detached import evidence, or null when there is no importable completed result.</returns>
    public static ParsedRun? Parse(string directory)
    {
        directory = Canonical(directory);
        string workflow = Path.Combine(directory, "run.json");
        string fuzz = Path.Combine(directory, "report.json");
        string completion = Path.Combine(directory, "campaign-completion.json");
        if (File.Exists(workflow))
        {
            return ParseWorkflow(directory, workflow);
        }

        if (File.Exists(completion))
        {
            return ParseCampaign(directory, completion);
        }

        if (File.Exists(fuzz))
        {
            return ParseFuzz(directory, fuzz);
        }

        if (File.Exists(Path.Combine(directory, "historical-completion.json")))
        {
            return HistoricalHistoryReports.Parse(directory);
        }

        string[] captures = Directory.GetFiles(directory, "*.results.json", SearchOption.TopDirectoryOnly);
        return captures.Length > 0 ? ParseCaptures(directory, captures) : null;
    }

    /// <summary>Imports terminal CMake workflow evidence and preserves measurement comparability metadata.</summary>
    /// <remarks>
    /// Running/missing-status or unfinished reports are skipped; unsupported statuses fail. Source bytes are
    /// hashed. If start/end Host identity or fingerprint disagrees, the comparison fingerprint is left null
    /// rather than asserting that measurements shared one environment. This does not verify a durable ledger row.
    /// </remarks>
    private static ParsedRun? ParseWorkflow(string directory, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        Version(root, path, 1);
        string? status = Str(root, "status");
        if (status is null or "running")
        {
            return null;
        }

        if (status is not ("succeeded" or "failed" or "canceled"))
        {
            throw new InvalidDataException($"{path}: unsupported workflow status '{status}'.");
        }

        string? identity = Str(root, "workflow");
        if (string.IsNullOrWhiteSpace(identity) || !root.TryGetProperty("nodes", out JsonElement nodes)
                                                || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path}: workflow and nodes are required.");
        }

        ParsedRun run = new()
        {
            Run = new(Id(directory), directory, "workflow", identity,
                Str(root, "manifestHash"), Time(root, "startedAt"), Time(root, "finishedAt"),
                status, null, Hash(bytes))
        };
        if (run.Run.FinishedAt is null)
        {
            return null;
        }

        JsonElement? environment = Obj(root, "environment");
        if (environment is { } host)
        {
            HistoryHostEnvironment? details = JsonSerializer.Deserialize(host, HistoryJson.Default.HistoryHostEnvironment);
            HistoryHostEnvironment? end = Obj(root, "environmentAtEnd") is { } endHost
                ? JsonSerializer.Deserialize(endHost, HistoryJson.Default.HistoryHostEnvironment)
                : null;
            run.Run = run.Run with
            {
                HostId = details?.HostId,
                EnvironmentFingerprint = end is not null &&
                                         (end.HostId != details?.HostId || end.Fingerprint != details?.Fingerprint)
                    ? null
                    : details?.Fingerprint,
                Environment = details
            };
        }

        Dictionary<string, string> builds = new(StringComparer.Ordinal);
        Dictionary<string, string> overrides = new(StringComparer.Ordinal);
        if (root.TryGetProperty("buildEnvironments", out JsonElement buildArray)
            && buildArray.ValueKind == JsonValueKind.Array)
        {
            run.Run = run.Run with
            {
                BuildEnvironments = JsonSerializer.Deserialize(buildArray,
                    HistoryJson.Default.ListHistoryBuildEnvironment)
            };
            foreach (JsonElement build in buildArray.EnumerateArray())
            {
                if (Str(build, "directory") is { } buildPath && Str(build, "fingerprint") is { } hash)
                {
                    builds[buildPath + "\u001f" + Str(build, "configuration")] = hash;
                }
            }
        }

        AddArtifact(run, null, "workflow-report", path, run.Run.SourceHash);
        string eventJournal = Path.Combine(directory, "events.jsonl");
        if (File.Exists(eventJournal))
        {
            AddArtifact(run, null, "event-journal", eventJournal);
        }

        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(Str(node, "id"))
                                                       || string.IsNullOrWhiteSpace(Str(node, "status")))
            {
                throw new InvalidDataException($"{path}: invalid node entry.");
            }

            string id = Str(node, "id")!;
            if (Str(node, "environmentOverrideHash") is { } overrideHash)
            {
                overrides[id] = overrideHash;
            }

            if (Str(node, "kind") == "profile")
            {
                run.ProfileInputs[id] = (Json(node, "arguments"), Scenario(node));
            }

            run.Nodes.Add(new(run.Run.Id, id, Str(node, "kind") ?? "unknown",
                Str(node, "status")!, Num(node, "durationSeconds"), Json(node, "dependencies"),
                Str(node, "buildDirectory"), Str(node, "configuration"), Str(node, "target")));
            if (Num(node, "processDurationSeconds") is { } seconds
                && Str(node, "kind") == "run" && Str(node, "status") == "succeeded")
            {
                string? variant = TimingVariant(id);
                run.Measurements.Add(new(run.Run.Id, id, "process_wall_time", "s", seconds,
                    variant, Scenario(node), "run.json", "uninstrumented process wall time",
                    run.Measurements.Count(m => m.Metric == "process_wall_time"
                                                && m.Variant == variant), Json(node, "arguments"), null));
            }

            ImportDagFlowHarness(directory, run, node, id);
            AddArtifact(run, id, "stdout", Str(node, "stdout"));
            AddArtifact(run, id, "stderr", Str(node, "stderr"));
            JsonElement? artifact = Obj(node, "artifact");
            if (artifact is { } a)
            {
                AddArtifact(run, id, "executable", Str(a, "executable"), Str(a, "sha256"));
            }
            else if (Str(node, "kind") is "run" or "profile"
                     && Str(node, "executable") is { } executable
                     && Path.IsPathFullyQualified(executable))
            {
                AddArtifact(run, id, "executable", executable, Str(node, "executableSha256"));
            }
        }

        ImportProfile(directory, run);
        for (int i = 0; i < run.Measurements.Count; i++)
        {
            HistoryMeasurement measurement = run.Measurements[i];
            HistoryNode? node = measurement.NodeId is null
                ? run.Nodes.FirstOrDefault(n => n.Id == "counters_" + measurement.Variant
                                                || n.Id == "capture_" + measurement.Variant)
                : run.Nodes.FirstOrDefault(n => n.Id == measurement.NodeId);
            if (node is null || node.BuildDirectory is null || run.Run.EnvironmentFingerprint is null)
            {
                continue;
            }

            string key = node.BuildDirectory + "\u001f" + node.Configuration;
            if (!builds.TryGetValue(key, out string? buildHash))
            {
                continue;
            }

            run.Measurements[i] = measurement with
            {
                Environment = Hash(Encoding.UTF8.GetBytes(
                    run.Run.EnvironmentFingerprint + "\n" + buildHash + "\n"
                    + overrides.GetValueOrDefault(node.Id)))
            };
        }

        FinalizeHash(run);
        return run;
    }

    // Only import snapshots explicitly recorded in run.json. No directory search,
    // and no reading mutable artifacts from the native build tree.
    private static void ImportDagFlowHarness(string directory, ParsedRun run, JsonElement node, string id)
    {
        if (Obj(node, "outputs") is not { } outputs)
        {
            return;
        }

        if (outputs.TryGetProperty("dagflow-summary.json", out _))
        {
            ImportDagFlowHarnessEntry(directory, run, outputs, id, "dagflow-", null);
        }
        // Multiple harness results can originate from one native invoke.
        for (int index = 0; outputs.TryGetProperty($"dagflow-{index}-summary.json", out _); index++)
        {
            ImportDagFlowHarnessEntry(directory, run, outputs, id, $"dagflow-{index}-", $"harness-{index}");
        }
    }

    private static void ImportDagFlowHarnessEntry(string directory, ParsedRun run,
        JsonElement outputs, string id, string prefix, string? variant)
    {
        string Saved(string name)
        {
            string relative = Str(outputs, prefix + name)
                           ?? throw new InvalidDataException($"{id}: incomplete DagFlow harness snapshot.");
            if (Path.IsPathRooted(relative))
            {
                throw new InvalidDataException($"{id}: absolute harness snapshot path.");
            }

            string full = Path.GetFullPath(Path.Combine(directory, relative));
            string inside = Path.GetRelativePath(directory, full);
            return inside == ".." || inside.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                               || Path.IsPathRooted(inside) || !File.Exists(full)
                ? throw new InvalidDataException($"{id}: missing or escaping DagFlow snapshot {name}.")
                : full;
        }

        string manifestPath = Saved("manifest.json");
        string summaryPath = Saved("summary.json");
        string runsPath = Saved("runs.jsonl");
        foreach ((string? type, string? path) in new[]
                 {
                     ("dagflow-manifest", manifestPath),
                     ("dagflow-summary", summaryPath), ("dagflow-runs", runsPath)
                 })
        {
            AddArtifact(run, id, type, path, HashFile(path));
        }

        if (Str(outputs, prefix + "replay-binary") is { } executable)
        {
            string original = Path.GetFullPath(Path.Combine(directory, executable));
            if (!original.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !File.Exists(original))
            {
                throw new InvalidDataException($"{id}: invalid snapshotted replay binary.");
            }

            AddArtifact(run, id, "dagflow-replay-binary", original, HashFile(original));
        }

        using JsonDocument summary = Read(summaryPath);
        JsonElement root = summary.RootElement;
        if (Str(root, "status") != "passed" || Obj(root, "cases") is not { } cases)
        {
            throw new InvalidDataException($"{summaryPath}: harness did not pass or has no cases.");
        }
        // Older DagFlow generic harnesses omit manifest_sha256; the explicit
        // per-run copy is still immutable and independently hashed for history.
        string? hash = Str(root, "manifest_sha256");
        if (hash is not null &&
            !string.Equals(hash, Hash(File.ReadAllBytes(manifestPath)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{summaryPath}: manifest SHA-256 does not match the snapshot.");
        }

        using JsonDocument manifest = Read(manifestPath);
        JsonElement? manifestCases = Obj(manifest.RootElement, "cases");
        Dictionary<(string Case, string Metric), (double Scale, int Count)> expected = [];
        foreach (JsonProperty item in cases.EnumerateObject())
        {
            if (Obj(item.Value, "metrics") is not { } metrics)
            {
                continue;
            }

            foreach (JsonProperty metric in metrics.EnumerateObject())
            {
                if (Num(metric.Value, "scale") is not { } scale || scale <= 0
                                                                || Num(metric.Value, "count") is not { } n || n < 0 ||
                                                                n != Math.Truncate(n))
                {
                    throw new InvalidDataException($"{summaryPath}: invalid metric metadata.");
                }

                expected[(item.Name, metric.Name)] = (scale, checked((int)n));
            }
        }

        Dictionary<(string Case, string Metric), int> counts = [];
        foreach (string line in File.ReadLines(runsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument parsed = JsonDocument.Parse(line);
            JsonElement row = parsed.RootElement;
            if (hash is not null && Str(row, "manifest_sha256") is { } rowHash && rowHash != hash)
            {
                throw new InvalidDataException($"{runsPath}: manifest hash mismatch.");
            }

            if (!row.TryGetProperty("warmup", out JsonElement warmup) ||
                warmup.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException($"{runsPath}: invalid warmup flag.");
            }

            if (warmup.GetBoolean() || Str(row, "result") != "0")
            {
                continue;
            }

            if (Str(row, "case") is not { } caseName || Obj(row, "output") is not { } data)
            {
                throw new InvalidDataException($"{runsPath}: missing case or output.");
            }

            foreach (JsonProperty value in data.EnumerateObject())
            {
                (string Case, string Metric) key = (Case: caseName, Metric: value.Name);
                if (!expected.ContainsKey(key))
                {
                    continue;
                }

                if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetDouble(out double number) ||
                    !double.IsFinite(number))
                {
                    throw new InvalidDataException($"{runsPath}: nonfinite metric {value.Name}.");
                }

                int index = counts.GetValueOrDefault(key);
                counts[key] = index + 1;
                // Compare workloads by explicit harness case recipe (never by
                // binary SHA-256, which naturally changes across revisions).
                string? recipe = manifestCases is { } mc && mc.TryGetProperty(caseName, out JsonElement caseRecipe)
                    ? caseRecipe.GetRawText()
                    : null;
                run.Measurements.Add(new(run.Run.Id, id, value.Name, "", number,
                    variant, caseName, "dagflow-runs.jsonl", "dagflow-harness", index,
                    recipe, null));
            }
        }

        foreach (((string Case, string Metric) key, (double Scale, int Count) spec) in expected)
        {
            if (counts.GetValueOrDefault(key) != spec.Count)
            {
                throw new InvalidDataException(
                    $"{runsPath}: sample count does not match summary for {key.Case}/{key.Metric}.");
            }
        }
    }

    private static string? TimingVariant(string id)
    {
        if (!id.StartsWith("timing_", StringComparison.Ordinal))
        {
            return null;
        }

        string tail = id["timing_".Length..];
        int split = tail.LastIndexOf('_');
        return split > 0 && int.TryParse(tail[(split + 1)..], out _) ? tail[..split] : null;
    }

    private static string? Scenario(JsonElement node)
    {
        if (!node.TryGetProperty("arguments", out JsonElement args) || args.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string?[] values = [.. args.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())];
        for (int i = 0; i + 1 < values.Length; i++)
        {
            if (values[i] == "--scenario")
            {
                return values[i + 1];
            }
        }

        return null;
    }

    private static ParsedRun? ParseFuzz(string directory, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("layer", out _))
        {
            return null;
        }

        Version(root, path, 1);
        string? status = Str(root, "status");
        if (status is null || status == "running" || Time(root, "finishedAt") is null)
        {
            return null;
        }

        if (status is not ("succeeded" or "failed" or "canceled"))
        {
            throw new InvalidDataException($"{path}: unsupported fuzz status '{status}'.");
        }

        ParsedRun run = new()
        {
            Run = new(Id(directory), directory, "fuzz", Str(root, "layer"),
                null, Time(root, "startedAt"), Time(root, "finishedAt"), status, null, Hash(bytes))
        };
        AddArtifact(run, null, "fuzz-report", path, run.Run.SourceHash);
        AddArtifact(run, null, "executable", Str(root, "executable"), Str(root, "executableSha256"));
        if (root.TryGetProperty("crashArtifacts", out JsonElement crashes) && crashes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in crashes.EnumerateArray())
            {
                AddArtifact(run, null, "crash", item.GetString());
            }
        }

        return run;
    }

    /// <summary>Imports a terminal campaign only after matching its referenced report hash.</summary>
    /// <remarks>
    /// The marker must reference a leaf report name in the same directory. Missing/changed reports fail;
    /// a missing finish timestamp or nonterminal marker is skipped. Source identity combines marker and report
    /// hashes so changed completion evidence refreshes history rather than licensing campaign replay.
    /// </remarks>
    private static ParsedRun? ParseCampaign(string directory, string marker)
    {
        byte[] bytes = File.ReadAllBytes(marker);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        Version(root, marker, 1);
        string? name = Str(root, "report");
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
        {
            throw new InvalidDataException($"{marker}: report must be a file name in the same directory.");
        }

        string reportPath = Path.Combine(directory, name);
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException($"Campaign report referenced by {marker} is missing.", reportPath);
        }

        string reportHash = Hash(File.ReadAllBytes(reportPath));
        if (reportHash != Str(root, "reportSha256"))
        {
            throw new InvalidDataException($"{marker}: report hash changed after completion; rerun the campaign.");
        }

        string? status = Str(root, "status");
        if (status is not ("succeeded" or "failed" or "canceled")
            || Time(root, "finishedAt") is null)
        {
            return null;
        }

        string markerHash = Hash(bytes);
        ParsedRun run = new()
        {
            Run = new(Id(directory), directory, "campaign",
                Str(root, "campaign"), null, Time(root, "startedAt"), Time(root, "finishedAt"),
                status, null, Hash(Encoding.UTF8.GetBytes(markerHash + reportHash)))
        };
        AddArtifact(run, null, "campaign-completion", marker, markerHash);
        AddArtifact(run, null, "campaign-report", reportPath, reportHash);
        return run;
    }

    /// <summary>Combines top-level capture records into one history run without asserting cross-backend equivalence.</summary>
    /// <remarks>
    /// Each selected record must have capture ID, finish time and status. Incomplete records skip the run;
    /// unsupported statuses fail. Any failed/canceled capture makes the aggregate run failed. Raw trace hashes
    /// are recorded from capture metadata here, not independently verified as durable operation completion.
    /// </remarks>
    private static ParsedRun? ParseCaptures(string directory, string[] paths)
    {
        ParsedRun run = new()
        {
            Run = new(Id(directory), directory, "profile-captures", null,
                null, null, null, "succeeded", null, "")
        };
        foreach (string? path in paths.Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = Read(path);
            JsonElement root = doc.RootElement;
            if (Str(root, "captureId") is null || Time(root, "finishedAt") is null
                                               || Str(root, "status") is null)
            {
                return null;
            }

            string status = Str(root, "status")!;
            if (status is not ("complete" or "failed" or "canceled"))
            {
                throw new InvalidDataException($"{path}: unsupported capture status '{status}'.");
            }

            string id = Str(root, "captureId")!;
            run.Nodes.Add(new(run.Run.Id, id, "profile", status, null, null, null, null, null));
            AddArtifact(run, id, "capture-result", path, Hash(File.ReadAllBytes(path)));
            AddArtifact(run, id, "trace", Str(root, "rawTrace"), Str(root, "rawTraceSha256"),
                Json(root, "eventSource"));
            string manifest = Path.Combine(directory, id + ".manifest.json");
            if (File.Exists(manifest))
            {
                using JsonDocument md = Read(manifest);
                Version(md.RootElement, manifest, 1);
                AddArtifact(run, id, "capture-manifest", manifest, Hash(File.ReadAllBytes(manifest)));
            }

            if (status != "complete")
            {
                run.Run = run.Run with { Status = "failed" };
            }

            run.Run = run.Run with
            {
                StartedAt = run.Run.StartedAt ?? Time(root, "startedAt"),
                FinishedAt = Time(root, "finishedAt")
            };
        }

        run.Run = run.Run with
        {
            SourceHash = Hash(Encoding.UTF8.GetBytes(string.Join("|",
                paths.Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p))))))
        };
        ImportProfile(directory, run);
        FinalizeHash(run);
        return run;
    }

    /// <summary>Extends source identity with deterministically ordered small-artifact hash metadata.</summary>
    /// <remarks>
    /// Excludes trace and executable artifacts from this additional identity component. Does not open or
    /// rehash those large artifacts, establish a filesystem snapshot or guarantee that every source change
    /// will be detected independently of the parser's selected evidence.
    /// </remarks>
    private static void FinalizeHash(ParsedRun run)
    {
        IEnumerable<string> small = run.Artifacts.Where(a => a.Sha256 is not null
                                             && a.Type is not ("trace" or "executable"))
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .Select(a => a.Path + ":" + a.Sha256);
        run.Run = run.Run with
        {
            SourceHash = Hash(Encoding.UTF8.GetBytes(
                run.Run.SourceHash + "|" + string.Join("|", small)))
        };
    }

    /// <summary>Enriches detached run evidence with capture, analysis and counter metadata from the results directory.</summary>
    /// <remarks>
    /// Preserves backend/method/unit fields instead of equating counters across sources. Version checks and
    /// metadata reads may fail; database mutation is deferred until the caller has parsed the entire batch.
    /// Raw trace references/hashes are metadata; merely importing them does not attest current trace contents.
    /// </remarks>
    private static void ImportProfile(string directory, ParsedRun run)
    {
        foreach (string? path in Directory.GetFiles(directory, "*.results.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = Read(path);
            JsonElement root = doc.RootElement;
            string? id = Str(root, "captureId");
            if (id is null)
            {
                continue;
            }

            AddArtifact(run, id, "capture-result", path, Hash(File.ReadAllBytes(path)));
            AddArtifact(run, id, "trace", Str(root, "rawTrace"), Str(root, "rawTraceSha256"),
                Json(root, "eventSource"));
            string manifest = Path.Combine(directory, id + ".manifest.json");
            if (File.Exists(manifest))
            {
                using JsonDocument md = Read(manifest);
                Version(md.RootElement, manifest, 1);
                AddArtifact(run, id, "capture-manifest", manifest, Hash(File.ReadAllBytes(manifest)));
            }
        }

        string[] comparisons = ["comparison.json", "counter-comparison.json", "analysis.json", "runs.jsonl"];
        foreach (string? name in comparisons)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                if (name == "comparison.json")
                {
                    using JsonDocument doc = Read(path);
                    Version(doc.RootElement, path, 1);
                }

                AddArtifact(run, null, name, path, Hash(File.ReadAllBytes(path)));
            }
        }

        string counterPath = Path.Combine(directory, "counter-comparison.json");
        if (File.Exists(counterPath))
        {
            using JsonDocument doc = Read(counterPath);
            JsonElement root = doc.RootElement;
            if (Num(root, "schemaVersion") is not (1 or 2))
            {
                throw new InvalidDataException(
                    $"{counterPath}: unsupported counter-comparison schemaVersion; expected 1 or 2.");
            }

            string backend = Str(root, "backend") ?? "unknown";
            if (root.TryGetProperty("variants", out JsonElement variants) && variants.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement variant in variants.EnumerateArray())
                {
                    foreach (string? collection in new[] { "events", "derived" })
                    {
                        if (Obj(variant, collection) is { } events)
                        {
                            foreach (JsonProperty ev in events.EnumerateObject())
                            {
                                if (Num(ev.Value, "value") is { } value)
                                {
                                    run.Measurements.Add(new(run.Run.Id, null, ev.Name,
                                    Str(ev.Value, "unit") ?? "", value, Str(variant, "name"),
                                    run.ProfileInputs.GetValueOrDefault("counters_" + Str(variant, "name")).Scenario,
                                    "counter-comparison.json", collection == "derived"
                                        ? "perf stat derived" +
                                          (Str(ev.Value, "domain") is { } domain ? " " + domain : "")
                                        : backend, 0,
                                    run.ProfileInputs.GetValueOrDefault("counters_" + Str(variant, "name")).Workload,
                                    null));
                                }
                            }
                        }
                    }
                }
            }
        }

        string analysisPath = Path.Combine(directory, "analysis.json");
        HashSet<string> failedAnalyses = new(StringComparer.Ordinal);
        if (File.Exists(analysisPath))
        {
            using JsonDocument doc = Read(analysisPath);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{analysisPath}: expected an array of profile captures.");
            }

            foreach (JsonElement capture in doc.RootElement.EnumerateArray())
            {
                if (capture.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Str(capture, "analysisStatus") is "failed" or "capture_failed"
                    && Str(capture, "captureId") is { } failedId)
                {
                    _ = failedAnalyses.Add(failedId);
                }

                if (Str(capture, "backend") != "perf"
                    || Str(capture, "mode") != "counters"
                    || Obj(capture, "counters") is not { } counters)
                {
                    continue;
                }

                string? captureId = Str(capture, "captureId");
                string? variant = captureId is not null && captureId.StartsWith("counters_", StringComparison.Ordinal)
                    ? captureId["counters_".Length..]
                    : captureId is not null && captureId.StartsWith("capture_", StringComparison.Ordinal)
                        ? captureId["capture_".Length..]
                        : null;
                foreach (JsonProperty counter in counters.EnumerateObject())
                {
                    if (Num(counter.Value, "value") is not { } value)
                    {
                        continue;
                    }

                    if (run.Measurements.Any(m => m.Metric == counter.Name && m.Variant == variant
                                                                           && m.Method == "perf stat"))
                    {
                        continue;
                    }

                    run.Measurements.Add(new(run.Run.Id, captureId, counter.Name,
                        Str(counter.Value, "unit") ?? "", value, variant,
                        captureId is null ? null : run.ProfileInputs.GetValueOrDefault(captureId).Scenario,
                        "analysis.json", "perf stat", 0,
                        captureId is null ? null : run.ProfileInputs.GetValueOrDefault(captureId).Workload, null));
                }
            }
        }

        string normalized = Path.Combine(directory, "traces", "normalized");
        if (Directory.Exists(normalized))
        {
            foreach (string file in Directory.GetFiles(normalized, "*.stacks.json"))
            {
                string captureId = Path.GetFileName(file)[..^".stacks.json".Length];
                if (failedAnalyses.Contains(captureId))
                {
                    continue;
                }

                using JsonDocument parsed = Read(file);
                if (Num(parsed.RootElement, "schemaVersion") is not (1 or 2))
                {
                    throw new InvalidDataException(
                        $"{file}: unsupported normalized stack schemaVersion; expected 1 or 2.");
                }

                AddArtifact(run, captureId, "cpu-stacks", file, HashFile(file),
                    Str(parsed.RootElement, "analysisStatus"));
            }

            foreach (string file in Directory.GetFiles(normalized, "*.timeline.jsonl"))
            {
                AddArtifact(run, Path.GetFileName(file)[..^".timeline.jsonl".Length],
                    "scheduler-timeline", file, HashFile(file));
            }
        }

        if (Directory.Exists(normalized))
        {
            foreach (string? path in Directory.GetFiles(normalized, "*.etw.json").Order(StringComparer.Ordinal))
            {
                using JsonDocument doc = Read(path);
                JsonElement root = doc.RootElement;
                Version(root, path, 1);
                if (Str(root, "backend") != "etw")
                {
                    throw new InvalidDataException($"{path}: expected an ETW summary.");
                }

                string capture = Path.GetFileName(path)[..^".etw.json".Length];
                // CPU and scheduler captures are separate passes. Never pool their
                // ETW samples as if they were repeated measurements of one mode.
                string captureVariant = capture.StartsWith("scheduler_", StringComparison.Ordinal)
                    ? capture["scheduler_".Length..] + ".scheduler"
                    : capture.StartsWith("capture_", StringComparison.Ordinal)
                        ? capture["capture_".Length..] + ".cpu"
                        : capture;
                AddArtifact(run, capture, "etw-summary", path, Hash(File.ReadAllBytes(path)));
                foreach ((string? field, string? unit) in new[]
                         {
                             ("targetCpuSamples", "samples"), ("targetContextSwitchesIn", "events"),
                             ("targetContextSwitchesOut", "events"), ("targetReadyEvents", "events"),
                             ("readyLatencySamples", "intervals"),
                             ("readyLatencySumMilliseconds", "ms"),
                             ("scheduledRunningMilliseconds", "ms")
                         })
                {
                    if (Num(root, field) is { } value)
                    {
                        run.Measurements.Add(new(run.Run.Id, capture, field, unit, value,
                            captureVariant, run.ProfileInputs.GetValueOrDefault(capture).Scenario,
                            "traces/normalized/" + Path.GetFileName(path),
                            "ETW tracerpt target PID/time window " + (Str(root, "attribution") ?? "unknown"),
                            0, run.ProfileInputs.GetValueOrDefault(capture).Workload, null));
                    }
                }

                double? latencyCount = Num(root, "readyLatencySamples");
                double? latencySum = Num(root, "readyLatencySumMilliseconds");
                if (latencyCount is > 0 && latencySum is >= 0)
                {
                    run.Measurements.Add(new(run.Run.Id, capture, "readyLatencyMeanMilliseconds",
                        "ms", latencySum.Value / latencyCount.Value, captureVariant,
                        run.ProfileInputs.GetValueOrDefault(capture).Scenario,
                        "traces/normalized/" + Path.GetFileName(path),
                        "ETW derived ready-to-running latency; target PID/time window", 0,
                        run.ProfileInputs.GetValueOrDefault(capture).Workload, null));
                }
            }
        }
    }
}