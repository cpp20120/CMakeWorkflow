using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Campaigns;

public static class MutationCampaign
{
    private sealed record Mutant(string Unit, string Model, string Before, string After);

    public static async Task<int> RunAsync(string sourceRoot, string suite,
        string compiler, string seed, int cases, int steps, int timeoutSeconds,
        string reportPath, CancellationToken ct)
    {
        if (cases < 1 || steps < 1 || timeoutSeconds < 1)
            throw new ArgumentException("Mutation budgets must be positive.");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "CMakeWorkflow.Campaigns.Mutation.mutants.json")
            ?? throw new IOException("Embedded mutation catalog is missing.");
        using var catalog = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
        var root = catalog.RootElement;
        var runtime = root.GetProperty("runtime").EnumerateArray()
            .Select(x => x.GetString()!).ToArray();
        var models = root.GetProperty("models").EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.GetString()!);
        var selected = root.GetProperty("mutants").EnumerateObject()
            .Select(x => (x.Name, Value: new Mutant(
                x.Value.GetProperty("unit").GetString()!,
                x.Value.GetProperty("model").GetString()!,
                x.Value.GetProperty("before").GetString()!,
                x.Value.GetProperty("after").GetString()!)))
            .Where(x => suite == "all" || x.Name == suite || x.Name.StartsWith(suite + "-", StringComparison.Ordinal)
                || x.Value.Model == suite)
            .ToArray();
        if (selected.Length == 0) throw new ArgumentException($"Unknown mutation suite or mutant: {suite}");

        sourceRoot = Path.GetFullPath(sourceRoot);
        if (!File.Exists(Path.Combine(sourceRoot, "include/dagflow/dagflow.hpp")))
            throw new ArgumentException("--source-dir must point to a DagFlow source tree.");
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var report = new JsonObject
        {
            ["compiler"] = compiler, ["seed"] = seed, ["cases"] = cases,
            ["steps"] = steps, ["baseline"] = new JsonObject(), ["mutants"] = new JsonObject()
        };
        async Task Save() => await File.WriteAllTextAsync(reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);

        var temporary = Path.Combine(Path.GetTempPath(), "dagflow-mutations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var flags = new[] { "-std=c++23", "-O0", "-g0", "-pthread", "-DDAGFLOW_STATIC",
                "-DDAGFLOW_FUZZ_HOOKS", "-I", Path.Combine(sourceRoot, "include"),
                "-I", Path.Combine(sourceRoot, "tests") };
            var objects = new Dictionary<string, string>();
            foreach (var name in runtime)
            {
                var obj = Path.Combine(temporary, name + ".o");
                await Compile(Path.Combine(sourceRoot, "src", name + ".cpp"), obj, flags);
                objects[name] = obj;
            }
            var tests = new Dictionary<string, string>();
            foreach (var model in selected.Select(x => x.Value.Model).Distinct())
            {
                var obj = Path.Combine(temporary, model + ".o");
                await Compile(Path.Combine(sourceRoot, "tests", models[model]), obj, flags);
                tests[model] = obj;
                var binary = Path.Combine(temporary, "baseline-" + model);
                await Link(objects.Values, obj, binary);
                var (status, output) = await Execute(binary);
                var passed = status == "SURVIVED";
                ((JsonObject)report["baseline"]!)[model] = new JsonObject
                {
                    ["status"] = passed ? "PASS" : status, ["output"] = output
                };
                await Save();
                Console.WriteLine($"baseline/{model}: {(passed ? "PASS" : status)}");
                if (!passed) return 2;
            }
            foreach (var (name, mutant) in selected)
            {
                var watch = Stopwatch.StartNew();
                string status, output;
                try
                {
                    var isHeader = mutant.Unit.StartsWith("include/", StringComparison.Ordinal);
                    var path = isHeader ? Path.Combine(sourceRoot, mutant.Unit)
                        : Path.Combine(sourceRoot, "src", mutant.Unit + ".cpp");
                    var changed = ReplaceOnce(await File.ReadAllTextAsync(path, ct), mutant.Before, mutant.After);
                    var changedObjects = new Dictionary<string, string>(objects);
                    var test = tests[mutant.Model];
                    if (isHeader)
                    {
                        var overlay = Path.Combine(temporary, name);
                        var changedPath = Path.Combine(overlay, mutant.Unit);
                        Directory.CreateDirectory(Path.GetDirectoryName(changedPath)!);
                        await File.WriteAllTextAsync(changedPath, changed, ct);
                        var overlayFlags = new[] { "-I", Path.Combine(overlay, "include") }.Concat(flags).ToArray();
                        foreach (var unit in runtime)
                        {
                            var obj = Path.Combine(overlay, unit + ".o");
                            await Compile(Path.Combine(sourceRoot, "src", unit + ".cpp"), obj, overlayFlags);
                            changedObjects[unit] = obj;
                        }
                        test = Path.Combine(overlay, "test.o");
                        await Compile(Path.Combine(sourceRoot, "tests", models[mutant.Model]), test, overlayFlags);
                    }
                    else
                    {
                        var changedPath = Path.Combine(temporary, name + ".cpp");
                        await File.WriteAllTextAsync(changedPath, changed, ct);
                        var obj = Path.Combine(temporary, name + ".o");
                        await Compile(changedPath, obj, flags);
                        changedObjects[mutant.Unit] = obj;
                    }
                    var binary = Path.Combine(temporary, "run-" + name);
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
                    ["status"] = status, ["model"] = mutant.Model, ["source"] = mutant.Unit,
                    ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3), ["output"] = output
                };
                await Save();
                Console.WriteLine($"{name}: {status}");
            }
            var detected = ((JsonObject)report["mutants"]!).Count(x =>
                x.Value?["status"]?.GetValue<string>().StartsWith("KILLED(", StringComparison.Ordinal) == true);
            Console.WriteLine($"Protocol mutations: {detected}/{selected.Length} assertion-detected. Report: {reportPath}");
            return detected == selected.Length ? 0 : 1;

            async Task Compile(string source, string output, IEnumerable<string> options)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var result = await CommandExecution.RunAsync(compiler,
                    options.Concat(["-c", source, "-o", output]), sourceRoot, TimeSpan.FromSeconds(120), null, ct);
                if (result.TimedOut || result.ExitCode != 0)
                    throw new InvalidOperationException($"Compiling {source}: {Tail(result.Output, 3500)}");
            }
            async Task Link(IEnumerable<string> units, string test, string output)
            {
                var result = await CommandExecution.RunAsync(compiler,
                    new[] { "-pthread" }.Concat(units).Concat([test, "-o", output]), sourceRoot,
                    TimeSpan.FromSeconds(80), null, ct);
                if (result.TimedOut || result.ExitCode != 0)
                    throw new InvalidOperationException($"Linking {output}: {Tail(result.Output, 3000)}");
            }
            async Task<(string, string)> Execute(string binary)
            {
                var env = new Dictionary<string, string?>
                {
                    ["DAGFLOW_PROPERTY_SEED"] = seed,
                    ["DAGFLOW_PROPERTY_CASES"] = cases.ToString(),
                    ["DAGFLOW_PROPERTY_STEPS"] = steps.ToString()
                };
                var result = await CommandExecution.RunAsync(binary, [], sourceRoot,
                    TimeSpan.FromSeconds(timeoutSeconds), env, ct);
                var output = result.Output;
                if (result.TimedOut || result.ExitCode == 87 && output.Contains("schedule timeout:"))
                    return ("TIMEOUT", output);
                if (result.ExitCode == 0) return ("SURVIVED", output);
                if (result.ExitCode == 86 && output.Contains("schedule property failed:"))
                    return ("KILLED(schedule)", output);
                if (output.Contains("property failed:")) return ("KILLED(property)", output);
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
        var first = source.IndexOf(before, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected unique mutation anchor: {before[..Math.Min(90, before.Length)]}");
        return source[..first] + after + source[(first + before.Length)..];
    }

    private static string Tail(string text, int limit) => text.Length <= limit ? text : text[^limit..];
}
