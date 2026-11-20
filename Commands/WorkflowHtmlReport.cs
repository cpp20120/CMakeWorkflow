using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.History;
using CMakeWorkflow.History.Storage;

namespace CMakeWorkflow.Commands;

internal static class WorkflowHtmlReport
{
    private const int MaximumPerfReportCharacters = 128 * 1024;
    private static readonly string[] NodeFields =
    [
        "id", "kind", "target", "configuration", "status",
        "durationSeconds", "processDurationSeconds", "metrics", "processResources", "actionCacheHit", "arguments", "benchmarkTrial"
    ];
    private static readonly string[] CaptureFields =
    [
        "captureId", "backend", "mode", "captureStatus", "analysisStatus", "analysisError",
        "analysisWarnings", "counters", "derivedCounters", "summary"
    ];

    internal static async Task<int> ExecuteAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        string? journal = FindArtifact(directory, "run.json");
        JsonArray nodes = [];
        JsonArray warnings = [];
        ParsedRun harness = new() { Run = new("", directory, "workflow", null, null, null, null, "", null, "") };
        string? status = null;
        if (journal is not null)
        {
            WorkflowReport report = (await WorkflowQueries.ReportAsync(directory, ct)).Report;
            status = report.Status;
            if (report.Error is { } workflowError)
            {
                warnings.Add((JsonNode?)JsonValue.Create(workflowError));
            }

            foreach (WorkflowFinding finding in report.Findings)
            {
                warnings.Add((JsonNode?)JsonValue.Create($"{finding.Severity} · {finding.NodeId} · {finding.Message}"));
            }

            foreach (NodeResult node in report.Nodes)
            {
                JsonElement serialized = JsonSerializer.SerializeToElement(node, WorkflowJson.Default.NodeResult);
                JsonObject evidence = JsonNode.Parse(serialized.GetRawText())!.AsObject();
                JsonObject row = [];
                foreach (string key in NodeFields)
                {
                    row[key] = evidence[key]?.DeepClone();
                }

                nodes.Add((JsonNode)row);
                if (node.Status == "succeeded")
                {
                    try
                    {
                        await HistoryReports.ImportDagFlowHarnessAsync(directory, harness, serialized, node.Id, GuardSnapshot, ct);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
                    {
                        _ = harness.Measurements.RemoveAll(sample => sample.NodeId == node.Id);
                        warnings.Add((JsonNode?)JsonValue.Create($"Harness evidence · {node.Id} · {error.Message}"));
                    }
                }
            }
        }

        JsonNode? analysis = await ReadJsonAsync(directory, "analysis.json", ct);
        if (journal is null && analysis is null)
        {
            throw new FileNotFoundException("The report requires run.json or analysis.json in the run directory.", directory);
        }

        JsonArray captures = [];
        if (analysis is not null)
        {
            foreach (JsonNode? item in analysis.AsArray())
            {
                if (item is JsonObject capture)
                {
                    captures.Add((JsonNode)await ReadCaptureAsync(directory, capture, ct));
                }
            }
        }

        JsonObject data = new()
        {
            ["name"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)),
            ["status"] = status,
            ["nodes"] = nodes,
            ["timing"] = await ReadJsonAsync(directory, "comparison.json", ct),
            ["counterComparison"] = await ReadJsonAsync(directory, "counter-comparison.json", ct),
            ["captures"] = captures,
            ["warnings"] = warnings,
            ["analysisLink"] = ArtifactLink(directory, "analysis.json"),
            ["measurements"] = JsonSerializer.SerializeToNode(harness.Measurements, HistoryJson.Default.ListHistoryMeasurement)
        };
        await WriteAsync(Path.Combine(directory, "report.html"), data, ct);
        return 0;

        void GuardSnapshot(string path)
        {
            if (FindArtifact(directory, Path.GetRelativePath(directory, path)) is null)
            {
                throw new InvalidDataException("Harness snapshot must be a regular file inside the run directory.");
            }
        }
    }

    internal static async Task WriteAsync(string output, JsonObject data, CancellationToken ct)
    {
        output = Path.GetFullPath(output);
        using Stream template = typeof(WorkflowHtmlReport).Assembly.GetManifestResourceStream("CMakeWorkflow.WorkflowReport.html")
            ?? throw new InvalidOperationException("Missing embedded HTML report template.");
        using StreamReader reader = new(template);
        string html = await reader.ReadToEndAsync(ct);
        foreach (string part in new[] { "Profile", "Charts" })
        {
            using Stream script = typeof(WorkflowHtmlReport).Assembly
                .GetManifestResourceStream($"CMakeWorkflow.Report{part}.js")
                ?? throw new InvalidOperationException($"Missing embedded report script: {part}.");
            using StreamReader source = new(script);
            html = html.Replace($"/*REPORT_{part.ToUpperInvariant()}*/", await source.ReadToEndAsync(ct), StringComparison.Ordinal);
        }
        // Neutralize HTML-significant '<' even if a JsonNode retains raw source tokens.
        string payload = data.ToJsonString().Replace("<", "\\u003c", StringComparison.Ordinal);
        string parent = Path.GetDirectoryName(output)!;
        _ = Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".report-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(staging, html.Replace("/*REPORT_DATA*/", payload, StringComparison.Ordinal), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(staging, output, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }

        Console.WriteLine(output);
    }

    private static async Task<JsonObject> ReadCaptureAsync(string directory, JsonObject capture, CancellationToken ct)
    {
        JsonObject row = [];
        foreach (string key in CaptureFields)
        {
            row[key] = capture[key]?.DeepClone();
        }

        // Use filenames owned by the analyzers. Artifact paths supplied inside
        // a downloaded analysis document cannot select files for publication.
        string? id = capture["captureId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || id.Length > 200
            || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return row;
        }

        string normalized = Path.Combine("traces", "normalized");
        string stackPath = Path.Combine(normalized, id + ".stacks.json");
        string pmcPath = Path.Combine(normalized, id + ".etw-pmc.json");
        row["normalizedLink"] = ArtifactLink(directory, stackPath) ?? ArtifactLink(directory, pmcPath)
            ?? ArtifactLink(directory, Path.Combine(normalized, id + ".etw.json"))
            ?? ArtifactLink(directory, Path.Combine(normalized, id + ".json"));
        row["rawPerfLink"] = ArtifactLink(directory, Path.Combine("traces", "raw", id, "run.perf.data"));
        if (await ReadJsonAsync(directory, id + ".results.json", ct) is JsonObject result)
        {
            row["startedAt"] = result["startedAt"]?.DeepClone();
            row["finishedAt"] = result["finishedAt"]?.DeepClone();
            row["targetStartedAt"] = result["targetStartedAt"]?.DeepClone();
            row["targetFinishedAt"] = result["targetFinishedAt"]?.DeepClone();
        }

        if (capture["stacks"] is not null)
        {
            row["stacks"] = await ReadJsonAsync(directory, stackPath, ct);
        }

        if (capture["method"]?.GetValue<string>() == "etw-pmc-overflow")
        {
            row["pmc"] = await ReadJsonAsync(directory, pmcPath, ct);
        }

        if (capture["timeline"] is not null)
        {
            row["timeline"] = await ReadTimelineAsync(directory, Path.Combine(normalized, id + ".timeline.jsonl"), ct);
        }

        if (capture["backend"]?.GetValue<string>() == "perf" && capture["mode"]?.GetValue<string>() != "counters"
            && capture["report"] is not null
            && FindArtifact(directory, id + ".perf-report.txt") is { } perfReport)
        {
            row["perfReportLink"] = ArtifactLink(directory, id + ".perf-report.txt");
            await using FileStream stream = File.OpenRead(perfReport);
            using StreamReader reader = new(stream);
            char[] excerpt = new char[MaximumPerfReportCharacters + 1];
            int length = await reader.ReadBlockAsync(excerpt.AsMemory(), ct);
            row["perfReport"] = new string(excerpt, 0, Math.Min(length, MaximumPerfReportCharacters));
            row["perfReportTruncated"] = length > MaximumPerfReportCharacters;
        }

        return row;
    }

    private static async Task<JsonArray?> ReadTimelineAsync(string directory, string relative, CancellationToken ct)
    {
        string? path = FindArtifact(directory, relative);
        if (path is null)
        {
            return null;
        }

        JsonArray events = [];
        await foreach (string line in File.ReadLinesAsync(path, ct))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                events.Add(JsonNode.Parse(line));
            }
        }

        return events;
    }

    private static async Task<JsonNode?> ReadJsonAsync(string directory, string relative, CancellationToken ct)
    {
        string? path = FindArtifact(directory, relative);
        if (path is null)
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? FindArtifact(string directory, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            return null;
        }

        string path = directory;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part is "" or "." or "..")
            {
                return null;
            }

            path = Path.Combine(path, part);
            if ((!File.Exists(path) && !Directory.Exists(path))
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }
        }

        return File.Exists(path) ? path : null;
    }

    private static string? ArtifactLink(string directory, string relative) => FindArtifact(directory, relative) is null
        ? null : string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
}
