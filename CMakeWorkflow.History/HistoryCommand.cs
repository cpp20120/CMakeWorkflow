using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using CMakeWorkflow.History.Analysis;
using CMakeWorkflow.History.Models;
using CMakeWorkflow.History.Storage;

namespace CMakeWorkflow.History;

public static class HistoryCommand
{
    public static Task<int> ExecuteAsync(string[] args, CancellationToken ct) => ExecuteAsync(args, null, ct);

    internal static async Task<int> ExecuteAsync(string[] args, Func<JsonObject, Task>? html, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (args.Length == 0)
            {
                throw new ArgumentException(Usage);
            }

            string command = args[0];
            List<string> positional = [];
            Dictionary<string, string> options = new(StringComparer.Ordinal);
            HashSet<string> flags = new(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] is "--recursive" or "--json")
                {
                    _ = flags.Add(args[i]);
                    continue;
                }

                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Missing value for {args[i]}.");
                    }

                    options[args[i]] = args[++i];
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            HashSet<string> allowed = new(StringComparer.Ordinal)
            {
                "--db", "--limit", "--status", "--since", "--until", "--workflow",
                "--variant", "--metric", "--scenario", "--run", "--direction",
                "--min-percent", "--alpha"
            };
            if (options.Keys.Any(x => !allowed.Contains(x)))
            {
                throw new ArgumentException($"Unknown history option: {options.Keys.First(x => !allowed.Contains(x))}");
            }

            string dbPath = options.GetValueOrDefault("--db") ?? HistoryStore.DefaultDatabasePath;
            using HistoryStore db = new(dbPath);
            bool json = flags.Contains("--json");
            if (json && html is not null)
            {
                throw new ArgumentException("Select one output format: --json or --html.");
            }

            switch (command)
            {
                case "import" when positional.Count == 1:
                    {
                        if (html is not null)
                        {
                            throw new ArgumentException("history import does not support --html.");
                        }

                        HistoryImportResult? result = null;
                        string summary = await db.ImportAsync(positional[0], flags.Contains("--recursive"),
                        value => result = value, ct: ct);
                        Console.WriteLine(json
                            ? JsonSerializer.Serialize(
                                result ?? throw new InvalidDataException("History import completed without a result."),
                                HistoryJson.Default.HistoryImportResult)
                            : summary);
                        break;
                    }
                case "runs" when positional.Count == 0:
                    {
                        int limit = int.TryParse(options.GetValueOrDefault("--limit") ?? "50", out int value)
                                && value > 0
                        ? value
                        : throw new ArgumentException("--limit must be positive.");
                        string? metric = html is null ? null : options.GetValueOrDefault("--metric")
                            ?? throw new ArgumentException("history runs --html requires --metric NAME.");
                        string? variant = options.GetValueOrDefault("--variant");
                        string? scenario = html is null ? null : options.GetValueOrDefault("--scenario");
                        List<HistoryRun> runs = db.Runs(limit, options.GetValueOrDefault("--status"),
                            DateOption(options.GetValueOrDefault("--since"), "--since"),
                            DateOption(options.GetValueOrDefault("--until"), "--until"),
                            options.GetValueOrDefault("--workflow"), variant, metric, scenario, null, ct);
                        if (html is not null)
                        {
                            List<HistoryMeasurement> measurements = [];
                            foreach (HistoryRun run in runs)
                            {
                                measurements.AddRange(db.Metrics(run.Id, metric, variant, scenario, ct: ct));
                            }

                            await WriteHtmlAsync(html, new()
                            {
                                ["runs"] = JsonSerializer.SerializeToNode(runs, HistoryJson.Default.ListHistoryRun),
                                ["measurements"] = JsonSerializer.SerializeToNode(measurements, HistoryJson.Default.ListHistoryMeasurement)
                            });
                        }
                        else if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(runs, HistoryJson.Default.ListHistoryRun));
                        }
                        else
                        {
                            foreach (HistoryRun r in runs)
                            {
                                Console.WriteLine(
                                $"{r.Id}  {r.Status,-10} {r.StartedAt ?? "unknown",-26} {r.Workflow ?? r.Format}  {(r.SourceAvailable ? r.Directory : "[missing] " + r.Directory)}");
                            }
                        }

                        break;
                    }
                case "show" when positional.Count == 1 && html is null:
                    {
                        HistoryDetail detail = db.Show(positional[0], ct: ct);
                        if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(detail, HistoryJson.Default.HistoryDetail));
                        }
                        else
                        {
                            Console.WriteLine(
                                $"{detail.Run.Id} {detail.Run.Status} {detail.Run.Workflow ?? detail.Run.Format}");
                            Console.WriteLine(
                                $"Source: {detail.Run.Directory} ({(detail.Run.SourceAvailable ? "available" : "missing")})");
                            if (detail.Run.Environment is { } environment)
                            {
                                Console.WriteLine(
                                $"Host: {detail.Run.HostId ?? "unknown"}; CPU: {environment.CpuModel ?? "unknown"}; " +
                                $"architecture: {environment.Architecture ?? "unknown"}; logical CPUs: {environment.LogicalProcessors?.ToString(CultureInfo.CurrentCulture) ?? "unknown"}; " +
                                $"kernel: {environment.KernelVersion ?? "unknown"}; affinity: {environment.Affinity ?? "unknown"}");
                            }

                            foreach (HistoryBuildEnvironment build in detail.Run.BuildEnvironments ?? [])
                            {
                                Console.WriteLine(
                                $"Build: {build.Directory}; config: {build.Configuration ?? "unknown"}; " +
                                $"compiler: {build.CompilerId ?? "unknown"} {build.CompilerVersion ?? ""}; " +
                                $"flags: {build.Flags ?? "unknown"}");
                            }

                            foreach (HistoryNode n in detail.Nodes)
                            {
                                Console.WriteLine(
                                $"  {n.Id} {n.Kind} {n.Status} {n.DurationSeconds?.ToString("G", CultureInfo.CurrentCulture) ?? "unknown"}s");
                            }

                            Console.WriteLine(
                            $"Measurements: {detail.Measurements.Count}; artifacts: {detail.Artifacts.Count}");
                            foreach (HistoryArtifact? artifact in detail.Artifacts.Where(x => !x.Available))
                            {
                                Console.WriteLine($"  Missing artifact: {artifact.Path}");
                            }
                        }

                        break;
                    }
                case "metrics" when positional.Count == 0:
                case "query" when positional.Count == 0:
                case "matrix" or "sweep" when positional.Count == 0 && html is not null:
                    {
                        string? run = options.GetValueOrDefault("--run");
                        string? metric = options.GetValueOrDefault("--metric");
                        if (command == "metrics" && run is null)
                        {
                            throw new ArgumentException("history metrics requires --run ID.");
                        }

                        if (command is "query" or "matrix" or "sweep" && metric is null)
                        {
                            throw new ArgumentException($"history {command} requires --metric NAME.");
                        }

                        List<HistoryMeasurement> rows = db.Metrics(run, metric, options.GetValueOrDefault("--variant"), options.GetValueOrDefault("--scenario"), ct: ct);
                        if (html is not null)
                        {
                            await WriteHtmlAsync(html, new()
                            {
                                ["measurements"] = JsonSerializer.SerializeToNode(rows, HistoryJson.Default.ListHistoryMeasurement)
                            });
                        }
                        else if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(rows, HistoryJson.Default.ListHistoryMeasurement));
                        }
                        else
                        {
                            foreach (HistoryMeasurement m in rows)
                            {
                                Console.WriteLine(
                                $"{m.RunId} {m.Metric}={m.Value:G} {m.Unit} variant={m.Variant ?? "unknown"} scenario={m.Scenario ?? "unknown"} method={m.Method}");
                            }
                        }

                        break;
                    }
                case "host-summary" when positional.Count is >= 1 and <= 32 && html is null:
                    {
                        HistoryHostRow[] rows = [.. positional.Select(id => HistoryHostRow.From(db.Show(id, ct: ct)))];
                        if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(rows, HistoryJson.Default.HistoryHostRowArray));
                        }
                        else
                        {
                            static string Number(double? value) =>
                                value?.ToString("G6", CultureInfo.InvariantCulture) ?? "-";
                            Console.WriteLine("Run\tHost\tBuild (s)\tHit rate\tCounter scope\tMax node sampled RSS (B)\tSampled CPU (s)\tMemory stalls\tArtifact bytes\tResource method\tMemory stall event\tMemory stall unit\tMemory stall method");
                            foreach (HistoryHostRow row in rows)
                            {
                                Console.WriteLine($"{row.RunId}\t{row.HostId ?? "unknown"}\t{Number(row.BuildSeconds)}\t" +
                                                  $"{Number(row.CacheHitRate)}\t{row.CacheCounterScope ?? "-"}\t" +
                                                  $"{Number(row.PeakRssBytes)}\t{Number(row.CpuSeconds)}\t" +
                                                  $"{Number(row.MemoryStalls)}\t{Number(row.ArtifactBytes)}\t" +
                                                  $"{row.ResourceMethod ?? "-"}\t{row.MemoryStallEvent ?? "-"}\t{row.MemoryStallUnit ?? "-"}\t{row.MemoryStallMethod ?? "-"}");
                            }
                        }
                        break;
                    }
                case "compare" when positional.Count == 2:
                    {
                        HistoryDetail baseline = db.Show(positional[0], ct: ct);
                        HistoryDetail candidate = db.Show(positional[1], ct: ct);
                        List<HistoryDifference> rows = Compare(baseline, candidate);
                        if (html is not null)
                        {
                            await WriteHtmlAsync(html, new()
                            {
                                ["differences"] = JsonSerializer.SerializeToNode(rows, HistoryJson.Default.ListHistoryDifference),
                                ["measurements"] = PairMeasurements(baseline, candidate),
                                ["baseline"] = baseline.Run.Id,
                                ["candidate"] = candidate.Run.Id
                            });
                        }
                        else if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(rows, HistoryJson.Default.ListHistoryDifference));
                        }
                        else
                        {
                            foreach (HistoryDifference row in rows)
                            {
                                Console.WriteLine(
                                $"{row.Metric} [{row.Unit}, {row.Method}, {row.Variant ?? "unknown"}]: {row.Status}; " +
                                $"{row.Baseline?.Median.ToString("G", CultureInfo.CurrentCulture) ?? "-"} -> {row.Candidate?.Median.ToString("G", CultureInfo.CurrentCulture) ?? "-"}; " +
                                $"Δ={row.AbsoluteChange?.ToString("G", CultureInfo.CurrentCulture) ?? "-"} ({row.PercentChange?.ToString("G", CultureInfo.CurrentCulture) ?? "-"}%); {row.Reason}");
                            }
                        }

                        break;
                    }
                case "detect" when positional.Count == 2:
                    {
                        string metric = options.GetValueOrDefault("--metric")
                                 ?? throw new ArgumentException("history detect requires --metric NAME.");
                        string direction = options.GetValueOrDefault("--direction")
                                    ?? throw new ArgumentException(
                                        "history detect requires --direction higher-worse|lower-worse.");
                        if (!double.TryParse(options.GetValueOrDefault("--min-percent"),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double minimum))
                        {
                            throw new ArgumentException("history detect requires --min-percent N.");
                        }

                        if (!double.TryParse(options.GetValueOrDefault("--alpha", "0.05"),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha))
                        {
                            throw new ArgumentException("--alpha must be a number between zero and one.");
                        }

                        HistoryDetail baseline = db.Show(positional[0], ct: ct);
                        HistoryDetail candidate = db.Show(positional[1], ct: ct);
                        HistoryDetectionReport report = HistoryDetector.Detect(baseline, candidate,
                        metric, options.GetValueOrDefault("--variant"),
                        options.GetValueOrDefault("--scenario"), direction, minimum, alpha, ct);
                        if (html is not null)
                        {
                            await WriteHtmlAsync(html, new()
                            {
                                ["detection"] = JsonSerializer.SerializeToNode(report, HistoryJson.Default.HistoryDetectionReport),
                                ["measurements"] = PairMeasurements(baseline, candidate, metric),
                                ["baseline"] = baseline.Run.Id,
                                ["candidate"] = candidate.Run.Id
                            });
                        }
                        else if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(report,
                            HistoryJson.Default.HistoryDetectionReport));
                        }
                        else
                        {
                            foreach (HistoryDetection row in report.Results)
                            {
                                Console.WriteLine($"{row.Metric} [{row.Unit}, {row.Method}, {row.Variant ?? "unknown"}, "
                                              + $"{row.Scenario ?? "unknown"}]: {row.Status}; "
                                              + $"n={row.Baseline?.Count.ToString(CultureInfo.CurrentCulture) ?? "-"}/{row.Candidate?.Count.ToString(CultureInfo.CurrentCulture) ?? "-"}; "
                                              + $"median={row.Baseline?.Median.ToString("G", CultureInfo.CurrentCulture) ?? "-"} -> "
                                              + $"{row.Candidate?.Median.ToString("G", CultureInfo.CurrentCulture) ?? "-"}; "
                                              + $"Δ={row.PercentChange?.ToString("G", CultureInfo.CurrentCulture) ?? "-"}%; "
                                              + $"p={row.PValue?.ToString("G", CultureInfo.CurrentCulture) ?? "-"}; "
                                              + $"Holm p={row.AdjustedPValue?.ToString("G", CultureInfo.CurrentCulture) ?? "-"}; {row.Reason}");
                            }
                        }

                        break;
                    }
                default: throw new ArgumentException(Usage);
            }

            return 0;

            Task WriteHtmlAsync(Func<JsonObject, Task> write, JsonObject data)
            {
                data["name"] = $"History · {command}";
                data["historyMode"] = command;
                return write(data);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException
                                      or KeyNotFoundException)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] History: {e.Message}");
            return 1;
        }
    }

    private const string Usage = """
                                 history import PATH [--recursive] [--db PATH]
                                 history runs [--limit N] [--status STATUS] [--since ISO] [--until ISO] [--workflow TEXT] [--variant NAME] [--json]
                                 history show RUN_ID [--json]
                                 history metrics --run RUN_ID [--metric NAME] [--variant NAME] [--scenario NAME] [--json]
                                 history query --metric NAME [--run RUN_ID] [--variant NAME] [--json]
                                 history compare BASELINE_RUN CANDIDATE_RUN [--json]
                                 history host-summary RUN_ID [RUN_ID...] [--db PATH] [--json]
                                 history detect BASELINE_RUN CANDIDATE_RUN --metric NAME --direction higher-worse|lower-worse --min-percent N [--variant NAME] [--scenario NAME] [--alpha N] [--json]
                                 history matrix|sweep --metric NAME [--run ID] [--variant NAME] [--scenario NAME] --html OUTPUT.html
                                 """;

    private static JsonNode? PairMeasurements(HistoryDetail baseline, HistoryDetail candidate, string? metric = null) =>
        JsonSerializer.SerializeToNode([.. baseline.Measurements.Concat(candidate.Measurements)
                .Where(row => row.Metric != "artifact_result_bytes" && (metric is null || row.Metric == metric))],
            HistoryJson.Default.ListHistoryMeasurement);

    private static string? DateOption(string? value, string option)
    {
        return value is null
            ? null
            : !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? throw new ArgumentException($"{option} requires an ISO timestamp.")
            : parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static List<HistoryDifference> Compare(HistoryDetail baseline, HistoryDetail candidate) =>
        HistoryComparison.Compare(baseline, candidate);
}
