using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.Core;

namespace CMakeWorkflow.Campaigns.Mutation;

public static class QueueMutationCampaign
{
    private static readonly (string Name, string Before, string After)[] Mutants =
    [
        ("discard-push-payload", "cell.value = std::forward<U>(value);", "cell.value = value_type{};"),
        ("discard-pop-payload", "value = std::move(cell.value);", "value = value_type{};"),
        ("lie-about-full-push", "} else if (difference < 0) {\n        return false;",
            "} else if (difference < 0) {\n        return true;"),
        ("lie-about-successful-pop", "cell.sequence.store(position + N, std::memory_order_release);\n    return true;",
            "cell.sequence.store(position + N, std::memory_order_release);\n    return false;")
    ];

    public static async Task<int> RunAsync(string sourceRoot, string compiler, string seed,
        int cases, int steps, int timeoutSeconds, string reportPath, CancellationToken ct)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        reportPath = Path.GetFullPath(reportPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string header = Path.Combine(sourceRoot, "include/dagflow/detail/ring_mpmc.hpp");
        string source = await File.ReadAllTextAsync(header, ct);
        IEnumerable<(string, string)> variants = new[] { ("baseline", source) }.Concat(Mutants.Select(x =>
            (x.Name, Mutate(source, x.Name, x.Before, x.After))));
        JsonObject report = new()
        {
            ["compiler"] = compiler,
            ["seed"] = seed,
            ["cases"] = cases,
            ["steps"] = steps,
            ["results"] = new JsonObject()
        };
        string temporary = Path.Combine(Path.GetTempPath(), "dagflow-queue-mutations-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(temporary);
        try
        {
            foreach ((string? name, string? text) in variants)
            {
                string overlay = Path.Combine(temporary, name, "include/dagflow/detail");
                _ = Directory.CreateDirectory(overlay);
                await File.WriteAllTextAsync(Path.Combine(overlay, "ring_mpmc.hpp"), text, ct);
                string binary = Path.Combine(temporary, name, "history_tests");
                CommandResult build = await CommandExecution.RunAsync(compiler,
                    [
                        "-std=c++23", "-O0", "-pthread", "-I", Path.Combine(temporary, name, "include"),
                        "-I", Path.Combine(sourceRoot, "include"), "-I", Path.Combine(sourceRoot, "tests"),
                        Path.Combine(sourceRoot, "tests/concurrent_history_property_tests.cpp"), "-o", binary
                    ],
                    sourceRoot, TimeSpan.FromSeconds(90), null, ct);
                if (build.TimedOut || build.ExitCode != 0)
                {
                    await Console.Error.WriteLineAsync($"{name}: COMPILE_ERROR\n{build.Output}");
                    return 2;
                }

                CommandResult result = await CommandExecution.RunAsync(binary, [], sourceRoot,
                    TimeSpan.FromSeconds(timeoutSeconds), new Dictionary<string, string?>
                    {
                        ["DAGFLOW_PROPERTY_SEED"] = seed,
                        ["DAGFLOW_PROPERTY_CASES"] = cases.ToString(System.Globalization.CultureInfo.CurrentCulture),
                        ["DAGFLOW_PROPERTY_STEPS"] = steps.ToString(System.Globalization.CultureInfo.CurrentCulture)
                    }, ct);
                string status = result.TimedOut ? "TIMEOUT" : result.ExitCode == 0 ? "PASS" : "KILLED(assertion)";
                ((JsonObject)report["results"]!)[name] = new JsonObject
                {
                    ["status"] = status,
                    ["output"] = result.Output
                };
                await File.WriteAllTextAsync(reportPath,
                    report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
                Console.WriteLine($"{name}: {status}");
                if (name == "baseline" && status != "PASS")
                {
                    return 2;
                }
            }

            JsonObject outcomes = (JsonObject)report["results"]!;
            int killed = Mutants.Count(x => outcomes[x.Name]?["status"]?.GetValue<string>() == "KILLED(assertion)");
            return outcomes["baseline"]?["status"]?.GetValue<string>() == "PASS" && killed == Mutants.Length ? 0 : 1;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string ReplaceOnce(string source, string before, string after)
    {
        int index = source.IndexOf(before, StringComparison.Ordinal);
        return index < 0 || source.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0
            ? throw new InvalidOperationException($"Expected one queue mutation anchor: {before}")
            : source[..index] + after + source[(index + before.Length)..];
    }

    private static string Mutate(string source, string name, string before, string after)
    {
        if (name != "lie-about-full-push")
        {
            return ReplaceOnce(source, before, after);
        }

        const string signature = "bool push_impl(U&& value) noexcept {";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        return start < 0
            ? throw new InvalidOperationException("Queue push implementation not found.")
            : source[..start] + ReplaceOnce(source[start..], before, after);
    }
}