using System.Globalization;

namespace CMakeWorkflow;

internal static class BenchmarkGenerateCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] != "benchmark")
            throw new ArgumentException("workflow generate requires the benchmark recipe kind.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] programArguments = [];
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                programArguments = args[(i + 1)..];
                break;
            }

            var option = args[i];
            if (option is not ("--build-dir" or "--target" or "--config" or "--repeats"
                or "--timeout-seconds" or "--output"))
                throw new ArgumentException($"Unknown benchmark generation option: {option}");
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            if (!options.TryAdd(option, args[i]))
                throw new ArgumentException($"Duplicate benchmark generation option: {option}");
        }

        string Required(string name) => options.GetValueOrDefault(name)
                                        ?? throw new ArgumentException($"workflow generate benchmark requires {name}.");

        if (!int.TryParse(Required("--repeats"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var repeats))
            throw new ArgumentException("--repeats must be an integer.");
        if (!double.TryParse(Required("--timeout-seconds"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var timeout))
            throw new ArgumentException("--timeout-seconds must be a number.");
        var build = Path.GetFullPath(Required("--build-dir"));
        var output = Path.GetFullPath(Required("--output"));
        var recipe = new BenchmarkRecipeV1("benchmark", 1, build, Required("--target"),
            options.GetValueOrDefault("--config"), repeats, timeout, programArguments);
        await WorkflowManifestBytes.WriteNewAsync(output,
            BenchmarkWorkflowBuilder.Build(recipe), ct);
        Console.WriteLine(output);
        return 0;
    }
}