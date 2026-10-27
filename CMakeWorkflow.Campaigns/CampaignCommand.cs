namespace CMakeWorkflow.Campaigns;

public static class CampaignCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException(
            "Usage: CMakeWorkflow dagflow <mutation run|weak-memory run|fuzz seed|fuzz run|fuzz triage|fuzz merge> ...");
        var allowed = (args[0], args[1]) switch
        {
            ("mutation", "run") => new[] { "--source-dir", "--suite", "--cxx", "--seed",
                "--cases", "--steps", "--timeout", "--report" },
            ("weak-memory", "run") => new[] { "--source-dir", "--protocol", "--relacy",
                "--cxx", "--timeout", "--report" },
            ("fuzz", "seed") => new[] { "--source-dir", "--layer", "--output" },
            ("fuzz", "run") => new[] { "--build-dir", "--layer", "--backend", "--duration",
                "--timeout", "--corpus", "--results-dir", "--config", "--max-len", "--replay-only" },
            ("fuzz", "triage") => new[] { "--run-dir" },
            ("fuzz", "merge") => new[] { "--run-dir", "--output", "--coverage" },
            _ => throw new ArgumentException($"Unknown DagFlow campaign: {args[0]} {args[1]}")
        };
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!allowed.Contains(option)) throw new ArgumentException($"Unknown option: {option}");
            if (option is "--replay-only" or "--coverage") continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            index++;
        }
        var source = Option(args, "--source-dir") ?? Directory.GetCurrentDirectory();
        source = Path.GetFullPath(source);
        switch (args[0], args[1])
        {
            case ("mutation", "run"):
                if (Option(args, "--suite") == "queue")
                    return await QueueMutationCampaign.RunAsync(source,
                        Option(args, "--cxx") ?? "c++", Option(args, "--seed") ?? "496833221358",
                        Integer(args, "--cases", 32), Integer(args, "--steps", 36),
                        Integer(args, "--timeout", 20),
                        Option(args, "--report") ?? Path.Combine(source, "out/queue-mutations/report.json"), ct);
                return await MutationCampaign.RunAsync(source,
                    Option(args, "--suite") ?? "all", Option(args, "--cxx") ?? "c++",
                    Option(args, "--seed") ?? "496833221358",
                    Integer(args, "--cases", 48), Integer(args, "--steps", 48),
                    Integer(args, "--timeout", 30),
                    Option(args, "--report") ?? Path.Combine(source, "out/protocol-mutations/report.json"), ct);
            case ("fuzz", "seed"):
                return await FuzzSeedCampaign.RunAsync(
                    Option(args, "--layer") ?? throw new ArgumentException("--layer is required."),
                    Option(args, "--output") ?? Path.Combine(source, "fuzz/corpus"), ct);
            case ("fuzz", "run"):
                return await FuzzRunCampaign.RunAsync(new FuzzRunOptions(
                    Option(args, "--build-dir") ?? throw new ArgumentException("--build-dir is required."),
                    Option(args, "--layer") ?? throw new ArgumentException("--layer is required."),
                    Option(args, "--backend"), Option(args, "--corpus"),
                    Option(args, "--results-dir") ?? "out/fuzz-runs",
                    Option(args, "--config"), Integer(args, "--duration", 60),
                    Integer(args, "--timeout", 15), Integer(args, "--max-len", 4096),
                    args.Contains("--replay-only")), ct);
            case ("fuzz", "triage"):
                return await FuzzRunCampaign.TriageAsync(
                    Option(args, "--run-dir") ?? throw new ArgumentException("--run-dir is required."), ct);
            case ("fuzz", "merge"):
                return await FuzzRunCampaign.MergeAsync(
                    Option(args, "--run-dir") ?? throw new ArgumentException("--run-dir is required."),
                    Option(args, "--output"), args.Contains("--coverage"), ct);
            case ("weak-memory", "run"):
                return await WeakMemoryCampaign.RunAsync(source,
                    Option(args, "--protocol") ?? "all",
                    Option(args, "--relacy") ?? throw new ArgumentException("--relacy is required."),
                    Option(args, "--cxx") ?? "c++", Integer(args, "--timeout", 60),
                    Option(args, "--report") ?? Path.Combine(source, "out/weak-memory/report.json"), ct);
            default:
                throw new ArgumentException($"Unknown DagFlow campaign: {args[0]} {args[1]}");
        }
    }

    private static int Integer(string[] args, string name, int fallback) =>
        Option(args, name) is { } text ? int.TryParse(text, out var value) ? value
            : throw new ArgumentException($"{name} must be an integer.") : fallback;

    private static string? Option(string[] args, string name)
    {
        var result = (string?)null;
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] != name) continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{name} requires a value.");
            result = args[index + 1];
        }
        return result;
    }
}
