using System.Text.Json;
using System.Globalization;

namespace CMakeWorkflow.History;

public static class HistoryCommand
{
    public static Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (args.Length == 0) throw new ArgumentException(Usage);
            var command = args[0];
            var positional = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] is "--recursive" or "--json") { flags.Add(args[i]); continue; }
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Missing value for {args[i]}.");
                    options[args[i]] = args[++i];
                }
                else positional.Add(args[i]);
            }
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "--db", "--limit", "--status", "--since", "--until", "--workflow",
                  "--variant", "--metric", "--scenario", "--run", "--direction",
                  "--min-percent", "--alpha" };
            if (options.Keys.Any(x => !allowed.Contains(x)))
                throw new ArgumentException($"Unknown history option: {options.Keys.First(x => !allowed.Contains(x))}");
            var dbPath = options.GetValueOrDefault("--db") ?? HistoryStore.DefaultDatabasePath;
            using var db = new HistoryStore(dbPath);
            var json = flags.Contains("--json");
            switch (command)
            {
                case "import" when positional.Count == 1:
                {
                    HistoryImportResult? result = null;
                    var summary = db.Import(positional[0], flags.Contains("--recursive"),
                        value => result = value);
                    Console.WriteLine(json
                        ? JsonSerializer.Serialize(result!, HistoryJson.Default.HistoryImportResult)
                        : summary);
                    break;
                }
                case "runs" when positional.Count == 0:
                {
                    var limit = int.TryParse(options.GetValueOrDefault("--limit") ?? "50", out var value)
                        && value > 0 ? value : throw new ArgumentException("--limit must be positive.");
                    var runs = db.Runs(limit, options.GetValueOrDefault("--status"),
                        DateOption(options.GetValueOrDefault("--since"), "--since"),
                        DateOption(options.GetValueOrDefault("--until"), "--until"),
                        options.GetValueOrDefault("--workflow"), options.GetValueOrDefault("--variant"));
                    if (json) Console.WriteLine(JsonSerializer.Serialize(runs, HistoryJson.Default.ListHistoryRun));
                    else foreach (var r in runs)
                        Console.WriteLine($"{r.Id}  {r.Status,-10} {r.StartedAt ?? "unknown",-26} {r.Workflow ?? r.Format}  {(r.SourceAvailable ? r.Directory : "[missing] " + r.Directory)}");
                    break;
                }
                case "show" when positional.Count == 1:
                {
                    var detail = db.Show(positional[0]);
                    if (json) Console.WriteLine(JsonSerializer.Serialize(detail, HistoryJson.Default.HistoryDetail));
                    else
                    {
                        Console.WriteLine($"{detail.Run.Id} {detail.Run.Status} {detail.Run.Workflow ?? detail.Run.Format}");
                        Console.WriteLine($"Source: {detail.Run.Directory} ({(detail.Run.SourceAvailable ? "available" : "missing")})");
                        if (detail.Run.Environment is { } environment)
                            Console.WriteLine($"Host: {detail.Run.HostId ?? "unknown"}; CPU: {environment.CpuModel ?? "unknown"}; " +
                                $"architecture: {environment.Architecture ?? "unknown"}; logical CPUs: {environment.LogicalProcessors?.ToString() ?? "unknown"}; " +
                                $"kernel: {environment.KernelVersion ?? "unknown"}; affinity: {environment.Affinity ?? "unknown"}");
                        foreach (var build in detail.Run.BuildEnvironments ?? [])
                            Console.WriteLine($"Build: {build.Directory}; config: {build.Configuration ?? "unknown"}; " +
                                $"compiler: {build.CompilerId ?? "unknown"} {build.CompilerVersion ?? ""}; " +
                                $"flags: {build.Flags ?? "unknown"}");
                        foreach (var n in detail.Nodes)
                            Console.WriteLine($"  {n.Id} {n.Kind} {n.Status} {n.DurationSeconds?.ToString("G") ?? "unknown"}s");
                        Console.WriteLine($"Measurements: {detail.Measurements.Count}; artifacts: {detail.Artifacts.Count}");
                        foreach (var artifact in detail.Artifacts.Where(x => !x.Available))
                            Console.WriteLine($"  Missing artifact: {artifact.Path}");
                    }
                    break;
                }
                case "metrics" when positional.Count == 0:
                case "query" when positional.Count == 0:
                {
                    var run = options.GetValueOrDefault("--run");
                    var metric = options.GetValueOrDefault("--metric");
                    if (command == "metrics" && run is null)
                        throw new ArgumentException("history metrics requires --run ID.");
                    if (command == "query" && metric is null)
                        throw new ArgumentException("history query requires --metric NAME.");
                    var rows = db.Metrics(run, metric, options.GetValueOrDefault("--variant"),
                        options.GetValueOrDefault("--scenario"));
                    if (json) Console.WriteLine(JsonSerializer.Serialize(rows, HistoryJson.Default.ListHistoryMeasurement));
                    else foreach (var m in rows)
                        Console.WriteLine($"{m.RunId} {m.Metric}={m.Value:G} {m.Unit} variant={m.Variant ?? "unknown"} scenario={m.Scenario ?? "unknown"} method={m.Method}");
                    break;
                }
                case "compare" when positional.Count == 2:
                {
                    var rows = Compare(db.Show(positional[0]), db.Show(positional[1]));
                    if (json) Console.WriteLine(JsonSerializer.Serialize(rows, HistoryJson.Default.ListHistoryDifference));
                    else foreach (var row in rows)
                        Console.WriteLine($"{row.Metric} [{row.Unit}, {row.Method}, {row.Variant ?? "unknown"}]: {row.Status}; " +
                            $"{row.Baseline?.Median.ToString("G") ?? "-"} -> {row.Candidate?.Median.ToString("G") ?? "-"}; " +
                            $"Δ={row.AbsoluteChange?.ToString("G") ?? "-"} ({row.PercentChange?.ToString("G") ?? "-"}%); {row.Reason}");
                    break;
                }
                case "detect" when positional.Count == 2:
                {
                    var metric = options.GetValueOrDefault("--metric")
                        ?? throw new ArgumentException("history detect requires --metric NAME.");
                    var direction = options.GetValueOrDefault("--direction")
                        ?? throw new ArgumentException("history detect requires --direction higher-worse|lower-worse.");
                    if (!double.TryParse(options.GetValueOrDefault("--min-percent"),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum))
                        throw new ArgumentException("history detect requires --min-percent N.");
                    if (!double.TryParse(options.GetValueOrDefault("--alpha", "0.05"),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                        throw new ArgumentException("--alpha must be a number between zero and one.");
                    var report = HistoryDetector.Detect(db.Show(positional[0]), db.Show(positional[1]),
                        metric, options.GetValueOrDefault("--variant"),
                        options.GetValueOrDefault("--scenario"), direction, minimum, alpha, ct);
                    if (json)
                        Console.WriteLine(JsonSerializer.Serialize(report,
                            HistoryJson.Default.HistoryDetectionReport));
                    else
                        foreach (var row in report.Results)
                            Console.WriteLine($"{row.Metric} [{row.Unit}, {row.Method}, {row.Variant ?? "unknown"}, "
                                + $"{row.Scenario ?? "unknown"}]: {row.Status}; "
                                + $"n={row.Baseline?.Count.ToString() ?? "-"}/{row.Candidate?.Count.ToString() ?? "-"}; "
                                + $"median={row.Baseline?.Median.ToString("G") ?? "-"} -> "
                                + $"{row.Candidate?.Median.ToString("G") ?? "-"}; "
                                + $"Δ={row.PercentChange?.ToString("G") ?? "-"}%; "
                                + $"p={row.PValue?.ToString("G") ?? "-"}; "
                                + $"Holm p={row.AdjustedPValue?.ToString("G") ?? "-"}; {row.Reason}");
                    break;
                }
                default: throw new ArgumentException(Usage);
            }
            return Task.FromResult(0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException
            or KeyNotFoundException)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] History: {e.Message}");
            return Task.FromResult(1);
        }
    }
    private const string Usage = """
        history import PATH [--recursive] [--db PATH]
        history runs [--limit N] [--status STATUS] [--since ISO] [--until ISO] [--workflow TEXT] [--variant NAME] [--json]
        history show RUN_ID [--json]
        history metrics --run RUN_ID [--metric NAME] [--variant NAME] [--scenario NAME] [--json]
        history query --metric NAME [--run RUN_ID] [--variant NAME] [--json]
        history compare BASELINE_RUN CANDIDATE_RUN [--json]
        history detect BASELINE_RUN CANDIDATE_RUN --metric NAME --direction higher-worse|lower-worse --min-percent N [--variant NAME] [--scenario NAME] [--alpha N] [--json]
        """;
    private static string? DateOption(string? value, string option)
    {
        if (value is null) return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed))
            throw new ArgumentException($"{option} requires an ISO timestamp.");
        return parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }
    internal static List<HistoryDifference> Compare(HistoryDetail baseline, HistoryDetail candidate) =>
        HistoryComparison.Compare(baseline, candidate);
}
