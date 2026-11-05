using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Campaigns.Mutation;

public static class MutationCampaign
{
    private static readonly string[] ThreadingCompilerArguments =
    [
    "-pthread"
];
    private sealed record Mutant(string Unit, string Model, string Before, string After);

    public static async Task<int> RunAsync(string sourceRoot, string suite,
        string compiler, string seed, int cases, int steps, int timeoutSeconds,
        string reportPath, CancellationToken ct)
    {
        if (cases < 1 || steps < 1 || timeoutSeconds < 1)
        {
            throw new ArgumentException("Mutation budgets must be positive.");
        }

        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                                 "CMakeWorkflow.Campaigns.Mutation.mutants.json")
                             ?? throw new IOException("Embedded mutation catalog is missing.");
        using JsonDocument catalog = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
        JsonElement root = catalog.RootElement;
        string[] runtime = [.. root.GetProperty("runtime").EnumerateArray().Select(x => x.GetString()!)];
        Dictionary<string, string> models = root.GetProperty("models").EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.GetString()!);
        (string Name, Mutant Value)[] selected = [.. root.GetProperty("mutants").EnumerateObject()
            .Select(x => (x.Name, Value: new Mutant(
                x.Value.GetProperty("unit").GetString()!,
                x.Value.GetProperty("model").GetString()!,
                x.Value.GetProperty("before").GetString()!,
                x.Value.GetProperty("after").GetString()!)))
            .Where(x => suite == "all" || x.Name == suite || x.Name.StartsWith(suite + "-", StringComparison.Ordinal)
                        || x.Value.Model == suite)];
        if (selected.Length == 0)
        {
            throw new ArgumentException($"Unknown mutation suite or mutant: {suite}");
        }

        sourceRoot = Path.GetFullPath(sourceRoot);
        if (!File.Exists(Path.Combine(sourceRoot, "include/dagflow/dagflow.hpp")))
        {
            throw new ArgumentException("--source-dir must point to a DagFlow source tree.");
        }

        reportPath = Path.GetFullPath(reportPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        JsonObject report = new()
        {
            ["compiler"] = compiler,
            ["seed"] = seed,
            ["cases"] = cases,
            ["steps"] = steps,
            ["baseline"] = new JsonObject(),
            ["mutants"] = new JsonObject()
        };

        async Task Save() => await File.WriteAllTextAsync(reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);

        string temporary = Path.Combine(Path.GetTempPath(), "dagflow-mutations-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(temporary);
        try
        {
            string[] flags =
            [
                "-std=c++23", "-O0", "-g0", "-pthread", "-DDAGFLOW_STATIC",
                "-DDAGFLOW_FUZZ_HOOKS", "-I", Path.Combine(sourceRoot, "include"),
                "-I", Path.Combine(sourceRoot, "tests")
            ];
            Dictionary<string, string> objects = [];
            foreach (string? name in runtime)
            {
                string obj = Path.Combine(temporary, name + ".o");
                await Compile(Path.Combine(sourceRoot, "src", name + ".cpp"), obj, flags);
                objects[name] = obj;
            }

            Dictionary<string, string> tests = [];
            foreach (string? model in selected.Select(x => x.Value.Model).Distinct())
            {
                string obj = Path.Combine(temporary, model + ".o");
                await Compile(Path.Combine(sourceRoot, "tests", models[model]), obj, flags);
                tests[model] = obj;
                string binary = Path.Combine(temporary, "baseline-" + model);
                await Link(objects.Values, obj, binary);
                (string? status, string? output) = await Execute(binary);
                bool passed = status == "SURVIVED";
                ((JsonObject)report["baseline"]!)[model] = new JsonObject
                {
                    ["status"] = passed ? "PASS" : status,
                    ["output"] = output
                };
                await Save();
                Console.WriteLine($"baseline/{model}: {(passed ? "PASS" : status)}");
                if (!passed)
                {
                    return 2;
                }
            }

            foreach ((string? name, Mutant? mutant) in selected)
            {
                Stopwatch watch = Stopwatch.StartNew();
                string status, output;
                try
                {
                    bool isHeader = mutant.Unit.StartsWith("include/", StringComparison.Ordinal);
                    string path = isHeader
                        ? Path.Combine(sourceRoot, mutant.Unit)
                        : Path.Combine(sourceRoot, "src", mutant.Unit + ".cpp");
                    string changed = ReplaceOnce(await File.ReadAllTextAsync(path, ct), mutant.Before, mutant.After);
                    Dictionary<string, string> changedObjects = new(objects);
                    string test = tests[mutant.Model];
                    if (isHeader)
                    {
                        string overlay = Path.Combine(temporary, name);
                        string changedPath = Path.Combine(overlay, mutant.Unit);
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(changedPath)!);
                        await File.WriteAllTextAsync(changedPath, changed, ct);
                        string[] overlayFlags = ["-I", Path.Combine(overlay, "include"), .. flags];
                        foreach (string? unit in runtime)
                        {
                            string obj = Path.Combine(overlay, unit + ".o");
                            await Compile(Path.Combine(sourceRoot, "src", unit + ".cpp"), obj, overlayFlags);
                            changedObjects[unit] = obj;
                        }

                        test = Path.Combine(overlay, "test.o");
                        await Compile(Path.Combine(sourceRoot, "tests", models[mutant.Model]), test, overlayFlags);
                    }
                    else
                    {
                        string changedPath = Path.Combine(temporary, name + ".cpp");
                        await File.WriteAllTextAsync(changedPath, changed, ct);
                        string obj = Path.Combine(temporary, name + ".o");
                        await Compile(changedPath, obj, flags);
                        changedObjects[mutant.Unit] = obj;
                    }

                    string binary = Path.Combine(temporary, "run-" + name);
                    await Link(changedObjects.Values, test, binary);
                    (status, output) = await Execute(binary);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    status = "BUILD_ERROR";
                    output = error.Message;
                }

                ((JsonObject)report["mutants"]!)[name] = new JsonObject
                {
                    ["status"] = status,
                    ["model"] = mutant.Model,
                    ["source"] = mutant.Unit,
                    ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3),
                    ["output"] = output
                };
                await Save();
                Console.WriteLine($"{name}: {status}");
            }

            int detected = ((JsonObject)report["mutants"]!).Count(x =>
                x.Value?["status"]?.GetValue<string>().StartsWith("KILLED(", StringComparison.Ordinal) == true);
            Console.WriteLine(
                $"Protocol mutations: {detected}/{selected.Length} assertion-detected. Report: {reportPath}");
            return detected == selected.Length ? 0 : 1;

            async Task Compile(string source, string output, IEnumerable<string> options)
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                CommandResult result = await CommandExecution.RunAsync(compiler,
                    options.Concat(["-c", source, "-o", output]), sourceRoot, TimeSpan.FromSeconds(120), null, ct);
                if (result.TimedOut || result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Compiling {source}: {Tail(result.Output, 3500)}");
                }
            }

            async Task Link(IEnumerable<string> units, string test, string output)
            {
                CommandResult result = await CommandExecution.RunAsync(compiler,
                    ThreadingCompilerArguments.Concat(units).Concat([test, "-o", output]), sourceRoot,
                    TimeSpan.FromSeconds(80), null, ct);
                if (result.TimedOut || result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Linking {output}: {Tail(result.Output, 3000)}");
                }
            }

            async Task<(string, string)> Execute(string binary)
            {
                Dictionary<string, string?> env = new()
                {
                    ["DAGFLOW_PROPERTY_SEED"] = seed,
                    ["DAGFLOW_PROPERTY_CASES"] = cases.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    ["DAGFLOW_PROPERTY_STEPS"] = steps.ToString(System.Globalization.CultureInfo.CurrentCulture)
                };
                CommandResult result = await CommandExecution.RunAsync(binary, [], sourceRoot,
                    TimeSpan.FromSeconds(timeoutSeconds), env, ct);
                string output = result.Output;
                if (result.TimedOut || (result.ExitCode == 87 && output.Contains("schedule timeout:", StringComparison.Ordinal)))
                {
                    return ("TIMEOUT", output);
                }

                if (result.ExitCode == 0)
                {
                    return ("SURVIVED", output);
                }

                if (result.ExitCode == 86 && output.Contains("schedule property failed:", StringComparison.Ordinal))
                {
                    return ("KILLED(schedule)", output);
                }

                if (output.Contains("property failed:", StringComparison.Ordinal))
                {
                    return ("KILLED(property)", output);
                }

                return ($"CRASH(exit={result.ExitCode})", output);
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string ReplaceOnce(string source, string before, string after)
    {
        int first = source.IndexOf(before, StringComparison.Ordinal);
        return first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0
            ? throw new InvalidOperationException(
                $"Expected unique mutation anchor: {before[..Math.Min(90, before.Length)]}")
            : source[..first] + after + source[(first + before.Length)..];
    }

    private static string Tail(string text, int limit) => text.Length <= limit ? text : text[^limit..];
}