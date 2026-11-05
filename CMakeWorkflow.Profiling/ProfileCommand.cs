using System.Text.Json;

namespace CMakeWorkflow.Profiling;

public static class ProfileCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct,
        Action<string>? onRunDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException("Usage: profile <run|analyze> ...");
        }

        if (args[0] == "flamegraph")
        {
            if (args.Length != 3)
            {
                throw new ArgumentException("profile flamegraph PERF_DATA OUTPUT_JSON");
            }

            _ = await DagFlowHistoricalFlamegraph.DecodeAsync(args[1], args[2], ct);
            return 0;
        }

        if (args[0] == "render-historical")
        {
            return args.Length != 2
                ? throw new ArgumentException("profile render-historical DIRECTORY")
                : await DagFlowHistoricalReport.RenderAsync(args[1], ct);
        }

        if (args[0] == "run-historical")
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            bool resume = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--resume")
                {
                    resume = true;
                    continue;
                }

                if (args[i] is not ("--source-dir" or "--output" or "--trials"
                        or "--stat-trials" or "--jobs") || i + 1 >= args.Length
                                                        || !values.TryAdd(args[i], args[++i]))
                {
                    throw new ArgumentException("Invalid historical profile option.");
                }
            }

            int Count(string name, int fallback) => values.TryGetValue(name, out string? text)
                ? int.TryParse(text, out int parsed)
                    ? parsed
                    : throw new ArgumentException(name + " must be an integer.")
                : fallback;

            return await DagFlowHistoricalCapture.RunAsync(new DagFlowHistoricalOptions(
                values.GetValueOrDefault("--source-dir", Directory.GetCurrentDirectory()),
                values.GetValueOrDefault("--output") ?? throw new ArgumentException("--output is required."),
                Count("--trials", 5), Count("--stat-trials", 3), Count("--jobs", 4), resume), ct);
        }

        if (args[0] == "analyze")
        {
            return args.Length != 2
                ? throw new ArgumentException("profile analyze RUN_DIRECTORY")
                : await ProfileAnalyzer.AnalyzeAsync(args[1], ct);
        }

        if (args[0] != "run")
        {
            throw new ArgumentException("Usage: profile <run|analyze> ...");
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        List<(string Name, string BuildDirectory)> variants = [];
        List<string> targetArgs = [];
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                targetArgs.AddRange(args[(i + 1)..]);
                break;
            }

            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                                     || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid profile option: {args[i]}");
            }

            if (args[i] is not ("--build-dir" or "--target" or "--backend" or "--mode"
                or "--results-dir" or "--source-dir" or "--preset" or "--config" or "--timeout-seconds"
                or "--variant" or "--repeats" or "--campaign" or "--o3-build-dir"
                or "--lto-build-dir" or "--output"))
            {
                throw new ArgumentException($"Unknown profile option: {args[i]}");
            }

            if (args[i] == "--variant")
            {
                string[] pair = args[i + 1].Split('=', 2);
                if (pair.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(pair[0], "^[a-z][a-z0-9_]*$")
                                     || string.IsNullOrWhiteSpace(pair[1]))
                {
                    throw new ArgumentException("--variant requires name=BUILD_DIR (lowercase name).");
                }

                variants.Add((pair[0], Path.GetFullPath(pair[1])));
                i++;
                continue;
            }

            if (!options.TryAdd(args[i], args[i + 1]))
            {
                throw new ArgumentException($"Duplicate profile option: {args[i]}");
            }

            i++;
        }

        string Required(string key) => options.GetValueOrDefault(key)
                                       ?? throw new ArgumentException($"{key} is required.");

        if (options.TryGetValue("--campaign", out string? campaign))
        {
            if (campaign != "main-o3-lto" || variants.Count != 0 || options.ContainsKey("--build-dir"))
            {
                throw new ArgumentException("--campaign main-o3-lto requires two named build directories.");
            }

            variants.Add(("o3", Path.GetFullPath(Required("--o3-build-dir"))));
            variants.Add(("lto", Path.GetFullPath(Required("--lto-build-dir"))));
        }
        else if (variants.Count == 0)
        {
            variants.Add(("main", Path.GetFullPath(Required("--build-dir"))));
        }
        else if (options.ContainsKey("--build-dir") || options.ContainsKey("--o3-build-dir")
                                                    || options.ContainsKey("--lto-build-dir"))
        {
            throw new ArgumentException("Use --variant or --build-dir, not both.");
        }

        if (variants.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != variants.Count)
        {
            throw new ArgumentException("Profile variant names must be unique.");
        }

        if (variants.Select(x => Path.GetFullPath(x.BuildDirectory))
                .Distinct(WorkflowPlanner.PathComparer).Count() != variants.Count)
        {
            throw new ArgumentException("Profile variants require distinct build directories.");
        }

        string target = options.TryGetValue("--target", out string? targetOption) ? targetOption
            : campaign is not null ? "dagflow_stress_bench"
            : throw new ArgumentException("--target is required.");
        string output = Path.GetFullPath(options.GetValueOrDefault("--results-dir",
            options.GetValueOrDefault("--output", "out/profiles")));
        string repeatsText = options.GetValueOrDefault("--repeats", "3");
        if (!int.TryParse(repeatsText, out int repeats) || repeats is < 1 or > 100)
        {
            throw new ArgumentException("--repeats must be between 1 and 100.");
        }

        string? source = options.TryGetValue("--source-dir", out string? sourceOption)
            ? Path.GetFullPath(sourceOption)
            : null;
        string timeout = options.GetValueOrDefault("--timeout-seconds", "300");
        if (!double.TryParse(timeout, out double seconds) || seconds <= 0 || seconds > 86400)
        {
            throw new ArgumentException("--timeout-seconds must be positive and at most one day.");
        }

        _ = Directory.CreateDirectory(output);
        string? preset = options.GetValueOrDefault("--preset");
        if (preset is not null && source is null)
        {
            source = Directory.GetCurrentDirectory();
        }

        if (preset is not null && variants.Count != 1)
        {
            throw new ArgumentException("--preset with multiple variants is ambiguous; configure each tree first.");
        }

        WorkflowDocument document = ProfileWorkflowBuilder.Build(
            variants.Select(v => new ProfileVariant(v.Name, v.BuildDirectory)).ToArray(),
            target, source, options.GetValueOrDefault("--config"), preset, repeats,
            seconds, targetArgs, options.GetValueOrDefault("--backend", "auto"),
            options.GetValueOrDefault("--mode", "cpu"));
        string manifest = Path.Combine(output, "profile-workflow-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(manifest,
            JsonSerializer.Serialize(document, WorkflowJson.Default.WorkflowDocument) + "\n", ct);
        string? runDirectory = null;
        int result = await WorkflowCommand.ExecuteAsync("run", [manifest, "--results-dir", output], ct,
            new ProfileWorkflowExecutor(), directory =>
            {
                runDirectory = directory;
                onRunDirectory?.Invoke(directory);
            });
        return result != 0 || runDirectory is null ? result : await ProfileAnalyzer.AnalyzeAsync(runDirectory, ct);
    }
}
