using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Campaigns;

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
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var header = Path.Combine(sourceRoot, "include/dagflow/detail/ring_mpmc.hpp");
        var source = await File.ReadAllTextAsync(header, ct);
        var variants = new[] { ("baseline", source) }.Concat(Mutants.Select(x =>
            (x.Name, Mutate(source, x.Name, x.Before, x.After))));
        var report = new JsonObject { ["compiler"] = compiler, ["seed"] = seed,
            ["cases"] = cases, ["steps"] = steps, ["results"] = new JsonObject() };
        var temporary = Path.Combine(Path.GetTempPath(), "dagflow-queue-mutations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var (name, text) in variants)
            {
                var overlay = Path.Combine(temporary, name, "include/dagflow/detail");
                Directory.CreateDirectory(overlay);
                await File.WriteAllTextAsync(Path.Combine(overlay, "ring_mpmc.hpp"), text, ct);
                var binary = Path.Combine(temporary, name, "history_tests");
                var build = await CommandExecution.RunAsync(compiler,
                    ["-std=c++23", "-O0", "-pthread", "-I", Path.Combine(temporary, name, "include"),
                     "-I", Path.Combine(sourceRoot, "include"), "-I", Path.Combine(sourceRoot, "tests"),
                     Path.Combine(sourceRoot, "tests/concurrent_history_property_tests.cpp"), "-o", binary],
                    sourceRoot, TimeSpan.FromSeconds(90), null, ct);
                if (build.TimedOut || build.ExitCode != 0)
                {
                    Console.Error.WriteLine($"{name}: COMPILE_ERROR\n{build.Output}");
                    return 2;
                }
                var result = await CommandExecution.RunAsync(binary, [], sourceRoot,
                    TimeSpan.FromSeconds(timeoutSeconds), new Dictionary<string, string?>
                    {
                        ["DAGFLOW_PROPERTY_SEED"] = seed, ["DAGFLOW_PROPERTY_CASES"] = cases.ToString(),
                        ["DAGFLOW_PROPERTY_STEPS"] = steps.ToString()
                    }, ct);
                var status = result.TimedOut ? "TIMEOUT" : result.ExitCode == 0 ? "PASS" : "KILLED(assertion)";
                ((JsonObject)report["results"]!)[name] = new JsonObject
                {
                    ["status"] = status, ["output"] = result.Output
                };
                await File.WriteAllTextAsync(reportPath,
                    report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
                Console.WriteLine($"{name}: {status}");
                if (name == "baseline" && status != "PASS") return 2;
            }
            var outcomes = (JsonObject)report["results"]!;
            var killed = Mutants.Count(x => outcomes[x.Name]?["status"]?.GetValue<string>() == "KILLED(assertion)");
            return outcomes["baseline"]?["status"]?.GetValue<string>() == "PASS" && killed == Mutants.Length ? 0 : 1;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string ReplaceOnce(string source, string before, string after)
    {
        var index = source.IndexOf(before, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected one queue mutation anchor: {before}");
        return source[..index] + after + source[(index + before.Length)..];
    }

    private static string Mutate(string source, string name, string before, string after)
    {
        if (name != "lie-about-full-push") return ReplaceOnce(source, before, after);
        const string signature = "bool push_impl(U&& value) noexcept {";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Queue push implementation not found.");
        return source[..start] + ReplaceOnce(source[start..], before, after);
    }
}
