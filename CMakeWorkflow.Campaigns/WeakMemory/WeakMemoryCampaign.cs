using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Campaigns;

public static class WeakMemoryCampaign
{
    public static async Task<int> RunAsync(string sourceRoot, string protocol,
        string relacyRoot, string compiler, int timeoutSeconds, string reportPath,
        string? mutant, CancellationToken ct)
    {
        if (timeoutSeconds < 1) throw new ArgumentException("--timeout must be positive.");
        sourceRoot = Path.GetFullPath(sourceRoot);
        relacyRoot = Path.GetFullPath(relacyRoot);
        reportPath = Path.GetFullPath(reportPath);
        if (!File.Exists(Path.Combine(relacyRoot, "relacy/relacy.hpp")))
            throw new ArgumentException("--relacy must contain relacy/relacy.hpp.");
        var protocols = SourceCases.Sources;
        if (protocol != "all" && !protocols.ContainsKey(protocol))
            throw new ArgumentException($"Unknown protocol: {protocol}");
        var generator = new SourceCases(sourceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var report = new JsonObject
        {
            ["compiler"] = compiler, ["search"] = "per-check",
            ["execution_depth_limit"] = 200,
            ["sources"] = new JsonObject(), ["checks"] = new JsonObject()
        };

        async Task Save() => await File.WriteAllTextAsync(reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);

        var temporary = Path.Combine(Path.GetTempPath(), "dagflow-weak-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var revision = await CommandExecution.RunAsync("git", ["-C", relacyRoot, "rev-parse", "HEAD"],
                sourceRoot, TimeSpan.FromSeconds(timeoutSeconds), null, ct);
            report["relacy_revision"] = revision.ExitCode == 0 ? revision.Output.Trim() : "unknown";
            var version = await CommandExecution.RunAsync(compiler, ["--version"], sourceRoot,
                TimeSpan.FromSeconds(timeoutSeconds), null, ct);
            report["compiler_version"] = version.Output;
            var chosen = protocols.Keys.Where(name => protocol == "all" || name == protocol).ToArray();
            var variants = chosen.ToDictionary(name => name, name => generator.Generate(name),
                StringComparer.Ordinal);
            if (mutant is not null && !variants.Values.SelectMany(items => items)
                    .Any(item => item.Name == mutant && mutant != "baseline"))
                throw new ArgumentException($"Unknown mutant for protocol '{protocol}': {mutant}");
            foreach (var protocolName in chosen)
            {
                var sourceFiles = protocols[protocolName].Concat([
                    $"tests/weak_memory/{protocolName}_relacy_tests.cpp",
                    "tests/weak_memory/model_support.hpp"
                ]).Concat(protocolName switch
                {
                    "parking" => ["tests/weak_memory/parking_fixture.hpp"],
                    "idle_accounting" => ["tests/weak_memory/idle_accounting_fixture.hpp"],
                    "shutdown" =>
                    [
                        "tests/weak_memory/idle_accounting_fixture.hpp",
                        "tests/weak_memory/shutdown_fixture.hpp"
                    ],
                    _ => Array.Empty<string>()
                }).Distinct(StringComparer.Ordinal);
                foreach (var source in sourceFiles)
                {
                    var current = await CommandExecution.Sha256Async(Path.Combine(sourceRoot, source), ct);
                    ((JsonObject)report["sources"]!)[source] = current;
                }

                await Save();
                foreach (var variant in variants[protocolName])
                {
                    var name = variant.Name;
                    if (mutant is not null && name != "baseline" && name != mutant) continue;
                    var header = variant.Header;
                    var source = Path.Combine(temporary, protocolName + "_under_test.hpp");
                    await File.WriteAllTextAsync(source, header, ct);
                    var artifact = Path.Combine(Path.GetDirectoryName(reportPath)!, $"{protocolName}-{name}.hpp");
                    await File.WriteAllTextAsync(artifact, header, ct);
                    var binary = Path.Combine(temporary, protocolName + "-" + name);
                    var test = Path.Combine(sourceRoot, "tests/weak_memory", protocolName + "_relacy_tests.cpp");
                    var build = await CommandExecution.RunAsync(compiler,
                        [
                            "-std=c++23", "-O2", "-pthread", "-I", relacyRoot, "-I", temporary,
                            "-I", Path.Combine(sourceRoot, "include"), "-I",
                            Path.Combine(sourceRoot, "tests/weak_memory"), test, "-o", binary
                        ],
                        sourceRoot, TimeSpan.FromSeconds(timeoutSeconds), null, ct);
                    if (build.TimedOut || build.ExitCode != 0)
                    {
                        report["build_error"] = new JsonObject
                        {
                            ["variant"] = $"{protocolName}/{name}", ["output"] = build.Output,
                            ["timed_out"] = build.TimedOut
                        };
                        await Save();
                        return 2;
                    }

                    foreach (var suite in variant.Suites)
                    {
                        var suiteName = suite.Name;
                        var expected = suite.Expected;
                        var result = await CommandExecution.RunAsync(binary, [suiteName], sourceRoot,
                            TimeSpan.FromSeconds(timeoutSeconds), null, ct);
                        var match = Regex.Match(result.Output,
                            $@"^WEAK_MEMORY_RESULT {Regex.Escape(suiteName)} (.+) executions=(\d+)$",
                            RegexOptions.Multiline);
                        var config = Regex.Match(result.Output,
                            $@"^WEAK_MEMORY_CONFIG {Regex.Escape(suiteName)} search=(sched_full|sched_bound) context_bound=(\d+) depth=(\d+)$",
                            RegexOptions.Multiline);
                        var status = match.Success ? match.Groups[1].Value : "NO_CHECKER_RESULT";
                        var code = status switch
                        {
                            "SUCCESS" => 0, "USER ASSERT FAILED" or "DATA RACE" => 86,
                            "DEADLOCK" => 87, _ => -1
                        };
                        var passed = !result.TimedOut && config.Success && expected.Contains(status)
                                     && result.ExitCode == code;
                        var kind = name == "baseline"
                            ? "baseline"
                            : expected.SequenceEqual(["SUCCESS"])
                                ? "survivor-control"
                                : "counterexample";
                        ((JsonObject)report["checks"]!)[$"{protocolName}/{name}/{suiteName}"] = new JsonObject
                        {
                            ["kind"] = kind, ["matched_expectation"] = passed,
                            ["status"] = status,
                            ["expected"] =
                                new JsonArray(expected.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                            ["exit_code"] = result.ExitCode,
                            ["executions"] = match.Success ? int.Parse(match.Groups[2].Value) : null,
                            ["search"] = config.Success ? config.Groups[1].Value : null,
                            ["context_bound"] = config.Success ? int.Parse(config.Groups[2].Value) : null,
                            ["execution_depth_limit"] = config.Success ? int.Parse(config.Groups[3].Value) : null,
                            ["adapter"] = Path.GetFileName(artifact), ["output"] = result.Output
                        };
                        await Save();
                        Console.WriteLine(
                            $"{protocolName}/{name}/{suiteName}: {(passed ? "PASS" : "FAIL")} ({kind}: {status})");
                        if (!passed) return 1;
                    }
                }
            }

            Console.WriteLine($"Report: {reportPath}");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            report["infrastructure_error"] = error.Message;
            await Save();
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}