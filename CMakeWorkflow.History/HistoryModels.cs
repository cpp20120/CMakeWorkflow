using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.History;

public sealed record HistoryRun(string Id, string Directory, string Format, string? Workflow,
    string? ManifestHash, string? StartedAt, string? FinishedAt, string Status,
    string? Revision, string SourceHash, bool SourceAvailable = true,
    string? HostId = null, string? EnvironmentFingerprint = null,
    HistoryHostEnvironment? Environment = null,
    List<HistoryBuildEnvironment>? BuildEnvironments = null);
public sealed record HistoryHostEnvironment(string? HostId, string? CpuModel, string? Architecture,
    int? LogicalProcessors, int? AvailableProcessors, int? PhysicalPackages, int? PhysicalCores,
    string? OperatingSystem, string? KernelVersion, string? Affinity, string? Governor,
    string? Fingerprint);
public sealed record HistoryBuildEnvironment(string Directory, string? Configuration,
    string? Generator, string? Compiler, string? CompilerId, string? CompilerVersion,
    string? Flags, string Fingerprint);
public sealed record HistoryNode(string RunId, string Id, string Kind, string Status,
    double? DurationSeconds, string? Dependencies, string? BuildDirectory,
    string? Configuration, string? Target);
public sealed record HistoryMeasurement(string RunId, string? NodeId, string Metric,
    string Unit, double Value, string? Variant, string? Scenario, string Source,
    string Method, int SampleIndex, string? Workload, string? Environment);
public sealed record HistoryArtifact(string RunId, string? NodeId, string Type,
    string Path, string? Sha256, string? Metadata, bool Available = true);
public sealed record HistoryDetail(HistoryRun Run, List<HistoryNode> Nodes,
    List<HistoryMeasurement> Measurements, List<HistoryArtifact> Artifacts);
public sealed record HistoryStats(int Count, double Median, double Min, double Max,
    double Mean, double StandardDeviation);
public sealed record HistoryDifference(string Metric, string Unit, string? Variant,
    string? Scenario, string Method, string Status, string? Reason,
    HistoryStats? Baseline, HistoryStats? Candidate, double? AbsoluteChange,
    double? PercentChange);
public sealed record HistoryImportResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
public sealed record HistoryDetectionReport(int SchemaVersion, string BaselineRun,
    string CandidateRun, string Metric, string Direction, double MinimumEffectPercent,
    double Alpha, string Test, List<HistoryDetection> Results);
public sealed record HistoryDetection(string Metric, string Unit, string? Variant,
    string? Scenario, string Method, string Status, string Reason,
    HistoryStats? Baseline, HistoryStats? Candidate, double? AbsoluteChange,
    double? PercentChange, double? PValue, double? AdjustedPValue,
    int? Permutations, bool? Exact);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<HistoryRun>))]
[JsonSerializable(typeof(HistoryDetail))]
[JsonSerializable(typeof(List<HistoryMeasurement>))]
[JsonSerializable(typeof(List<HistoryDifference>))]
[JsonSerializable(typeof(HistoryHostEnvironment))]
[JsonSerializable(typeof(List<HistoryBuildEnvironment>))]
[JsonSerializable(typeof(HistoryImportResult))]
[JsonSerializable(typeof(HistoryDetectionReport))]
internal partial class HistoryJson : JsonSerializerContext;

internal sealed class ParsedRun
{
    public required HistoryRun Run { get; set; }
    public List<HistoryNode> Nodes { get; } = [];
    public List<HistoryMeasurement> Measurements { get; } = [];
    public List<HistoryArtifact> Artifacts { get; } = [];
    public Dictionary<string, (string? Workload, string? Scenario)> ProfileInputs { get; } = [];
}

internal static class HistoryReports
{
    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null
            or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    private static string? Json(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetRawText() : null;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string? Time(JsonElement e, string name)
    {
        var value = Str(e, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time)
            ? time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : null;
    }
    private static string Id(string directory) => Hash(Encoding.UTF8.GetBytes(directory));
    private static string Canonical(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    private static string? RelativeArtifact(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Path.GetFullPath(Path.IsPathFullyQualified(name) ? name : Path.Combine(directory, name));
    }
    private static void AddArtifact(ParsedRun run, string? node, string type, string? path,
        string? hash = null, string? metadata = null)
    {
        var full = RelativeArtifact(run.Run.Directory, path);
        if (full is not null) run.Artifacts.Add(new(run.Run.Id, node, type, full, hash, metadata));
    }
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllBytes(path));
    private static void Version(JsonElement root, string path, int expected, bool required = true)
    {
        if (!root.TryGetProperty("schemaVersion", out var v))
        {
            if (required) throw new InvalidDataException($"{path}: missing schemaVersion; expected {expected}.");
            return;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n != expected)
            throw new InvalidDataException($"{path}: unsupported schemaVersion {v}; expected {expected}.");
    }
    public static ParsedRun? Parse(string directory)
    {
        directory = Canonical(directory);
        var workflow = Path.Combine(directory, "run.json");
        var fuzz = Path.Combine(directory, "report.json");
        var completion = Path.Combine(directory, "campaign-completion.json");
        if (File.Exists(workflow)) return ParseWorkflow(directory, workflow);
        if (File.Exists(completion)) return ParseCampaign(directory, completion);
        if (File.Exists(fuzz)) return ParseFuzz(directory, fuzz);
        var captures = Directory.GetFiles(directory, "*.results.json", SearchOption.TopDirectoryOnly);
        return captures.Length > 0 ? ParseCaptures(directory, captures) : null;
    }
    private static ParsedRun? ParseWorkflow(string directory, string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Version(root, path, 1);
        var status = Str(root, "status");
        if (status is null || status == "running") return null;
        if (status is not ("succeeded" or "failed" or "canceled"))
            throw new InvalidDataException($"{path}: unsupported workflow status '{status}'.");
        var identity = Str(root, "workflow");
        if (string.IsNullOrWhiteSpace(identity) || !root.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: workflow and nodes are required.");
        var run = new ParsedRun { Run = new(Id(directory), directory, "workflow", identity,
            Str(root, "manifestHash"), Time(root, "startedAt"), Time(root, "finishedAt"),
            status, null, Hash(bytes)) };
        if (run.Run.FinishedAt is null) return null;
        var environment = Obj(root, "environment");
        if (environment is { } host)
        {
            var details = JsonSerializer.Deserialize(host, HistoryJson.Default.HistoryHostEnvironment);
            var end = Obj(root, "environmentAtEnd") is { } endHost
                ? JsonSerializer.Deserialize(endHost, HistoryJson.Default.HistoryHostEnvironment) : null;
            run.Run = run.Run with { HostId = details?.HostId,
                EnvironmentFingerprint = end is not null &&
                    (end.HostId != details?.HostId || end.Fingerprint != details?.Fingerprint)
                    ? null : details?.Fingerprint, Environment = details };
        }
        var builds = new Dictionary<string, string>(StringComparer.Ordinal);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("buildEnvironments", out var buildArray)
            && buildArray.ValueKind == JsonValueKind.Array)
        {
            run.Run = run.Run with { BuildEnvironments = JsonSerializer.Deserialize(buildArray,
                HistoryJson.Default.ListHistoryBuildEnvironment) };
            foreach (var build in buildArray.EnumerateArray())
                if (Str(build, "directory") is { } buildPath && Str(build, "fingerprint") is { } hash)
                    builds[buildPath + "\u001f" + Str(build, "configuration")] = hash;
        }
        AddArtifact(run, null, "workflow-report", path, run.Run.SourceHash);
        var eventJournal = Path.Combine(directory, "events.jsonl");
        if (File.Exists(eventJournal)) AddArtifact(run, null, "event-journal", eventJournal);
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(Str(node, "id"))
                || string.IsNullOrWhiteSpace(Str(node, "status")))
                throw new InvalidDataException($"{path}: invalid node entry.");
            var id = Str(node, "id")!;
            if (Str(node, "environmentOverrideHash") is { } overrideHash)
                overrides[id] = overrideHash;
            if (Str(node, "kind") == "profile")
                run.ProfileInputs[id] = (Json(node, "arguments"), Scenario(node));
            run.Nodes.Add(new(run.Run.Id, id, Str(node, "kind") ?? "unknown",
                Str(node, "status")!, Num(node, "durationSeconds"), Json(node, "dependencies"),
                Str(node, "buildDirectory"), Str(node, "configuration"), Str(node, "target")));
            if (Num(node, "processDurationSeconds") is { } seconds
                && Str(node, "kind") == "run" && Str(node, "status") == "succeeded")
            {
                var variant = TimingVariant(id);
                run.Measurements.Add(new(run.Run.Id, id, "process_wall_time", "s", seconds,
                    variant, Scenario(node), "run.json", "uninstrumented process wall time",
                    run.Measurements.Count(m => m.Metric == "process_wall_time"
                        && m.Variant == variant), Json(node, "arguments"), null));
            }
            ImportDagFlowHarness(directory, run, node, id);
            AddArtifact(run, id, "stdout", Str(node, "stdout"));
            AddArtifact(run, id, "stderr", Str(node, "stderr"));
            var artifact = Obj(node, "artifact");
            if (artifact is { } a)
                AddArtifact(run, id, "executable", Str(a, "executable"), Str(a, "sha256"));
            else if (Str(node, "kind") is "run" or "profile"
                && Str(node, "executable") is { } executable
                && Path.IsPathFullyQualified(executable))
                AddArtifact(run, id, "executable", executable, Str(node, "executableSha256"));
        }
        ImportProfile(directory, run);
        for (var i = 0; i < run.Measurements.Count; i++)
        {
            var measurement = run.Measurements[i];
            var node = measurement.NodeId is null
                ? run.Nodes.FirstOrDefault(n => n.Id == "counters_" + measurement.Variant
                    || n.Id == "capture_" + measurement.Variant)
                : run.Nodes.FirstOrDefault(n => n.Id == measurement.NodeId);
            if (node is null || node.BuildDirectory is null || run.Run.EnvironmentFingerprint is null)
                continue;
            var key = node.BuildDirectory + "\u001f" + node.Configuration;
            if (!builds.TryGetValue(key, out var buildHash)) continue;
            run.Measurements[i] = measurement with { Environment = Hash(Encoding.UTF8.GetBytes(
                run.Run.EnvironmentFingerprint + "\n" + buildHash + "\n"
                + overrides.GetValueOrDefault(node.Id))) };
        }
        FinalizeHash(run);
        return run;
    }
    // Only import snapshots explicitly recorded in run.json. No directory search,
    // and no reading mutable artifacts from the native build tree.
    private static void ImportDagFlowHarness(string directory, ParsedRun run, JsonElement node, string id)
    {
        if (Obj(node, "outputs") is not { } outputs) return;
        if (outputs.TryGetProperty("dagflow-summary.json", out _))
            ImportDagFlowHarnessEntry(directory, run, outputs, id, "dagflow-", null);
        // Multiple harness results can originate from one native invoke.
        for (var index = 0; outputs.TryGetProperty($"dagflow-{index}-summary.json", out _); index++)
            ImportDagFlowHarnessEntry(directory, run, outputs, id, $"dagflow-{index}-", $"harness-{index}");
    }
    private static void ImportDagFlowHarnessEntry(string directory, ParsedRun run,
        JsonElement outputs, string id, string prefix, string? variant)
    {
        string Saved(string name)
        {
            var relative = Str(outputs, prefix + name)
                ?? throw new InvalidDataException($"{id}: incomplete DagFlow harness snapshot.");
            if (Path.IsPathRooted(relative)) throw new InvalidDataException($"{id}: absolute harness snapshot path.");
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            var inside = Path.GetRelativePath(directory, full);
            if (inside == ".." || inside.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(inside) || !File.Exists(full))
                throw new InvalidDataException($"{id}: missing or escaping DagFlow snapshot {name}.");
            return full;
        }
        var manifestPath = Saved("manifest.json");
        var summaryPath = Saved("summary.json");
        var runsPath = Saved("runs.jsonl");
        foreach (var (type, path) in new[] { ("dagflow-manifest", manifestPath),
            ("dagflow-summary", summaryPath), ("dagflow-runs", runsPath) })
            AddArtifact(run, id, type, path, HashFile(path));
        if (Str(outputs, prefix + "replay-binary") is { } executable)
        {
            var original = Path.GetFullPath(Path.Combine(directory, executable));
            if (!original.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !File.Exists(original))
                throw new InvalidDataException($"{id}: invalid snapshotted replay binary.");
            AddArtifact(run, id, "dagflow-replay-binary", original, HashFile(original));
        }
        using var summary = Read(summaryPath);
        var root = summary.RootElement;
        if (Str(root, "status") != "passed" || Obj(root, "cases") is not { } cases)
            throw new InvalidDataException($"{summaryPath}: harness did not pass or has no cases.");
        // Older DagFlow generic harnesses omit manifest_sha256; the explicit
        // per-run copy is still immutable and independently hashed for history.
        var hash = Str(root, "manifest_sha256");
        if (hash is not null && !string.Equals(hash, Hash(File.ReadAllBytes(manifestPath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{summaryPath}: manifest SHA-256 does not match the snapshot.");
        using var manifest = Read(manifestPath);
        var manifestCases = Obj(manifest.RootElement, "cases");
        var expected = new Dictionary<(string Case, string Metric), (double Scale, int Count)>();
        foreach (var item in cases.EnumerateObject())
        {
            if (Obj(item.Value, "metrics") is not { } metrics) continue;
            foreach (var metric in metrics.EnumerateObject())
            {
                if (Num(metric.Value, "scale") is not { } scale || scale <= 0
                    || Num(metric.Value, "count") is not { } n || n < 0 || n != Math.Truncate(n))
                    throw new InvalidDataException($"{summaryPath}: invalid metric metadata.");
                expected[(item.Name, metric.Name)] = (scale, checked((int)n));
            }
        }
        var counts = new Dictionary<(string Case, string Metric), int>();
        foreach (var line in File.ReadLines(runsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var parsed = JsonDocument.Parse(line);
            var row = parsed.RootElement;
            if (hash is not null && Str(row, "manifest_sha256") is { } rowHash && rowHash != hash)
                throw new InvalidDataException($"{runsPath}: manifest hash mismatch.");
            if (!row.TryGetProperty("warmup", out var warmup) || warmup.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"{runsPath}: invalid warmup flag.");
            if (warmup.GetBoolean() || Str(row, "result") != "0") continue;
            if (Str(row, "case") is not { } caseName || Obj(row, "output") is not { } data)
                throw new InvalidDataException($"{runsPath}: missing case or output.");
            foreach (var value in data.EnumerateObject())
            {
                var key = (Case: caseName, Metric: value.Name);
                if (!expected.ContainsKey(key)) continue;
                if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new InvalidDataException($"{runsPath}: nonfinite metric {value.Name}.");
                var index = counts.GetValueOrDefault(key);
                counts[key] = index + 1;
                // Compare workloads by explicit harness case recipe (never by
                // binary SHA-256, which naturally changes across revisions).
                var recipe = manifestCases is { } mc && mc.TryGetProperty(caseName, out var caseRecipe)
                    ? caseRecipe.GetRawText() : null;
                run.Measurements.Add(new(run.Run.Id, id, value.Name, "", number,
                    variant, caseName, "dagflow-runs.jsonl", "dagflow-harness", index,
                    recipe, null));
            }
        }
        foreach (var (key, spec) in expected)
            if (counts.GetValueOrDefault(key) != spec.Count)
                throw new InvalidDataException($"{runsPath}: sample count does not match summary for {key.Case}/{key.Metric}.");
    }

    private static string? TimingVariant(string id)
    {
        if (!id.StartsWith("timing_", StringComparison.Ordinal)) return null;
        var tail = id["timing_".Length..];
        var split = tail.LastIndexOf('_');
        return split > 0 && int.TryParse(tail[(split + 1)..], out _) ? tail[..split] : null;
    }
    private static string? Scenario(JsonElement node)
    {
        if (!node.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Array)
            return null;
        var values = args.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()).ToArray();
        for (var i = 0; i + 1 < values.Length; i++)
            if (values[i] == "--scenario") return values[i + 1];
        return null;
    }
    private static ParsedRun? ParseFuzz(string directory, string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("layer", out _)) return null;
        Version(root, path, 1);
        var status = Str(root, "status");
        if (status is null || status == "running" || Time(root, "finishedAt") is null) return null;
        if (status is not ("succeeded" or "failed" or "canceled"))
            throw new InvalidDataException($"{path}: unsupported fuzz status '{status}'.");
        var run = new ParsedRun { Run = new(Id(directory), directory, "fuzz", Str(root, "layer"),
            null, Time(root, "startedAt"), Time(root, "finishedAt"), status, null, Hash(bytes)) };
        AddArtifact(run, null, "fuzz-report", path, run.Run.SourceHash);
        AddArtifact(run, null, "executable", Str(root, "executable"), Str(root, "executableSha256"));
        if (root.TryGetProperty("crashArtifacts", out var crashes) && crashes.ValueKind == JsonValueKind.Array)
            foreach (var item in crashes.EnumerateArray())
                AddArtifact(run, null, "crash", item.GetString());
        return run;
    }
    private static ParsedRun? ParseCampaign(string directory, string marker)
    {
        var bytes = File.ReadAllBytes(marker);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Version(root, marker, 1);
        var name = Str(root, "report");
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
            throw new InvalidDataException($"{marker}: report must be a file name in the same directory.");
        var reportPath = Path.Combine(directory, name);
        if (!File.Exists(reportPath))
            throw new FileNotFoundException($"Campaign report referenced by {marker} is missing.", reportPath);
        var reportHash = Hash(File.ReadAllBytes(reportPath));
        if (reportHash != Str(root, "reportSha256"))
            throw new InvalidDataException($"{marker}: report hash changed after completion; rerun the campaign.");
        var status = Str(root, "status");
        if (status is not ("succeeded" or "failed" or "canceled")
            || Time(root, "finishedAt") is null)
            return null;
        var markerHash = Hash(bytes);
        var run = new ParsedRun { Run = new(Id(directory), directory, "campaign",
            Str(root, "campaign"), null, Time(root, "startedAt"), Time(root, "finishedAt"),
            status, null, Hash(Encoding.UTF8.GetBytes(markerHash + reportHash))) };
        AddArtifact(run, null, "campaign-completion", marker, markerHash);
        AddArtifact(run, null, "campaign-report", reportPath, reportHash);
        return run;
    }
    private static ParsedRun? ParseCaptures(string directory, string[] paths)
    {
        var run = new ParsedRun { Run = new(Id(directory), directory, "profile-captures", null,
            null, null, null, "succeeded", null, "") };
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            using var doc = Read(path);
            var root = doc.RootElement;
            if (Str(root, "captureId") is null || Time(root, "finishedAt") is null
                || Str(root, "status") is null) return null;
            var status = Str(root, "status")!;
            if (status is not ("complete" or "failed" or "canceled"))
                throw new InvalidDataException($"{path}: unsupported capture status '{status}'.");
            var id = Str(root, "captureId")!;
            run.Nodes.Add(new(run.Run.Id, id, "profile", status, null, null, null, null, null));
            AddArtifact(run, id, "capture-result", path, Hash(File.ReadAllBytes(path)));
            AddArtifact(run, id, "trace", Str(root, "rawTrace"), Str(root, "rawTraceSha256"),
                Json(root, "eventSource"));
            var manifest = Path.Combine(directory, id + ".manifest.json");
            if (File.Exists(manifest))
            {
                using var md = Read(manifest);
                Version(md.RootElement, manifest, 1);
                AddArtifact(run, id, "capture-manifest", manifest, Hash(File.ReadAllBytes(manifest)));
            }
            if (status != "complete") run.Run = run.Run with { Status = "failed" };
            run.Run = run.Run with { StartedAt = run.Run.StartedAt ?? Time(root, "startedAt"),
                FinishedAt = Time(root, "finishedAt") };
        }
        run.Run = run.Run with { SourceHash = Hash(Encoding.UTF8.GetBytes(string.Join("|",
            paths.Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p)))))) };
        ImportProfile(directory, run);
        FinalizeHash(run);
        return run;
    }
    private static void FinalizeHash(ParsedRun run)
    {
        var small = run.Artifacts.Where(a => a.Sha256 is not null
            && a.Type is not ("trace" or "executable"))
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .Select(a => a.Path + ":" + a.Sha256);
        run.Run = run.Run with { SourceHash = Hash(Encoding.UTF8.GetBytes(
            run.Run.SourceHash + "|" + string.Join("|", small))) };
    }
    private static void ImportProfile(string directory, ParsedRun run)
    {
        foreach (var path in Directory.GetFiles(directory, "*.results.json").Order(StringComparer.Ordinal))
        {
            using var doc = Read(path);
            var root = doc.RootElement;
            var id = Str(root, "captureId");
            if (id is null) continue;
            AddArtifact(run, id, "capture-result", path, Hash(File.ReadAllBytes(path)));
            AddArtifact(run, id, "trace", Str(root, "rawTrace"), Str(root, "rawTraceSha256"),
                Json(root, "eventSource"));
            var manifest = Path.Combine(directory, id + ".manifest.json");
            if (File.Exists(manifest))
            {
                using var md = Read(manifest);
                Version(md.RootElement, manifest, 1);
                AddArtifact(run, id, "capture-manifest", manifest, Hash(File.ReadAllBytes(manifest)));
            }
        }
        var comparisons = new[] { "comparison.json", "counter-comparison.json", "analysis.json", "runs.jsonl" };
        foreach (var name in comparisons)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                if (name == "comparison.json")
                {
                    using var doc = Read(path);
                    Version(doc.RootElement, path, 1);
                }
                AddArtifact(run, null, name, path, Hash(File.ReadAllBytes(path)));
            }
        }
        var counterPath = Path.Combine(directory, "counter-comparison.json");
        if (File.Exists(counterPath))
        {
            using var doc = Read(counterPath);
            var root = doc.RootElement;
            Version(root, counterPath, 1);
            var backend = Str(root, "backend") ?? "unknown";
            if (root.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray())
                    foreach (var collection in new[] { "events", "derived" })
                        if (Obj(variant, collection) is { } events)
                            foreach (var ev in events.EnumerateObject())
                                if (Num(ev.Value, "value") is { } value)
                                    run.Measurements.Add(new(run.Run.Id, null, ev.Name,
                                        Str(ev.Value, "unit") ?? "", value, Str(variant, "name"),
                                        run.ProfileInputs.GetValueOrDefault("counters_" + Str(variant, "name")).Scenario,
                                        "counter-comparison.json", collection == "derived" ? "perf stat derived" : backend, 0,
                                        run.ProfileInputs.GetValueOrDefault("counters_" + Str(variant, "name")).Workload,
                                        null));
        }
        var analysisPath = Path.Combine(directory, "analysis.json");
        if (File.Exists(analysisPath))
        {
            using var doc = Read(analysisPath);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{analysisPath}: expected an array of profile captures.");
            foreach (var capture in doc.RootElement.EnumerateArray())
            {
                if (capture.ValueKind != JsonValueKind.Object
                    || Str(capture, "backend") != "perf"
                    || Str(capture, "mode") != "counters"
                    || Obj(capture, "counters") is not { } counters) continue;
                var captureId = Str(capture, "captureId");
                var variant = captureId is not null && captureId.StartsWith("counters_", StringComparison.Ordinal)
                    ? captureId["counters_".Length..]
                    : captureId is not null && captureId.StartsWith("capture_", StringComparison.Ordinal)
                        ? captureId["capture_".Length..] : null;
                foreach (var counter in counters.EnumerateObject())
                {
                    if (Num(counter.Value, "value") is not { } value) continue;
                    if (run.Measurements.Any(m => m.Metric == counter.Name && m.Variant == variant
                        && m.Method == "perf stat")) continue;
                    run.Measurements.Add(new(run.Run.Id, captureId, counter.Name,
                        Str(counter.Value, "unit") ?? "", value, variant,
                        captureId is null ? null : run.ProfileInputs.GetValueOrDefault(captureId).Scenario,
                        "analysis.json", "perf stat", 0,
                        captureId is null ? null : run.ProfileInputs.GetValueOrDefault(captureId).Workload, null));
                }
            }
        }
        var normalized = Path.Combine(directory, "traces", "normalized");
        if (Directory.Exists(normalized))
        {
            foreach (var file in Directory.GetFiles(normalized, "*.stacks.json"))
            {
                using var parsed = Read(file);
                Version(parsed.RootElement, file, 1);
                AddArtifact(run, Path.GetFileName(file)[..^".stacks.json".Length],
                    "cpu-stacks", file, HashFile(file));
            }
            foreach (var file in Directory.GetFiles(normalized, "*.timeline.jsonl"))
                AddArtifact(run, Path.GetFileName(file)[..^".timeline.jsonl".Length],
                    "scheduler-timeline", file, HashFile(file));
        }
        if (Directory.Exists(normalized))
            foreach (var path in Directory.GetFiles(normalized, "*.etw.json").Order(StringComparer.Ordinal))
            {
                using var doc = Read(path);
                var root = doc.RootElement;
                Version(root, path, 1);
                if (Str(root, "backend") != "etw")
                    throw new InvalidDataException($"{path}: expected an ETW summary.");
                var capture = Path.GetFileName(path)[..^".etw.json".Length];
                // CPU and scheduler captures are separate passes. Never pool their
                // ETW samples as if they were repeated measurements of one mode.
                var captureVariant = capture.StartsWith("scheduler_", StringComparison.Ordinal)
                    ? capture["scheduler_".Length..] + ".scheduler"
                    : capture.StartsWith("capture_", StringComparison.Ordinal)
                        ? capture["capture_".Length..] + ".cpu" : capture;
                AddArtifact(run, capture, "etw-summary", path, Hash(File.ReadAllBytes(path)));
                foreach (var (field, unit) in new[] {
                    ("targetCpuSamples", "samples"), ("targetContextSwitchesIn", "events"),
                    ("targetContextSwitchesOut", "events"), ("targetReadyEvents", "events"),
                    ("readyLatencySamples", "intervals"),
                    ("readyLatencySumMilliseconds", "ms"),
                    ("scheduledRunningMilliseconds", "ms") })
                    if (Num(root, field) is { } value)
                        run.Measurements.Add(new(run.Run.Id, capture, field, unit, value,
                            captureVariant, run.ProfileInputs.GetValueOrDefault(capture).Scenario,
                            "traces/normalized/" + Path.GetFileName(path),
                            "ETW tracerpt target PID/time window " + (Str(root, "attribution") ?? "unknown"),
                            0, run.ProfileInputs.GetValueOrDefault(capture).Workload, null));
                var latencyCount = Num(root, "readyLatencySamples");
                var latencySum = Num(root, "readyLatencySumMilliseconds");
                if (latencyCount is > 0 && latencySum is >= 0)
                    run.Measurements.Add(new(run.Run.Id, capture, "readyLatencyMeanMilliseconds",
                        "ms", latencySum.Value / latencyCount.Value, captureVariant,
                        run.ProfileInputs.GetValueOrDefault(capture).Scenario,
                        "traces/normalized/" + Path.GetFileName(path),
                        "ETW derived ready-to-running latency; target PID/time window", 0,
                        run.ProfileInputs.GetValueOrDefault(capture).Workload, null));
            }
    }
}
