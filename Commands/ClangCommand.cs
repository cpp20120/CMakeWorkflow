using CMakeWorkflow.Contracts.Workflows;
using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.CMake.Quality;
using CMakeWorkflow.Tooling.Tools;
using CMakeWorkflow.Developer;

namespace CMakeWorkflow.Commands;

internal static class ClangCommand
{
    private static readonly string[] FormattingModes = ["--check", "--apply", "--diff"];

    private const string Usage = """
        CMakeWorkflow format [--check|--apply|--diff] [--language cpp|cmake|all] [--target NAME]
          [--changed|--staged|--file PATH ...] [--include GLOB] [--exclude GLOB]
          [--tool-policy AllowSystem|ManagedOnly] [--batch-size N] [--json]
        CMakeWorkflow lint [--tool clang-tidy|clang-check|iwyu] [--checks GLOB] [--export-fixes DIR]
        CMakeWorkflow lint --apply-replacements DIR
        CMakeWorkflow analyze [--target NAME] [--preset NAME]
        Common options: --project DIR --jobs N --timeout-seconds N --executable PATH
        Build metadata: --build-dir DIR or --preset NAME, plus --configuration NAME for multi-config.
        A preset configures a dedicated local tooling build tree.
        """;

    internal static async Task<int> ExecuteAsync(string command, string[] args, CancellationToken ct)
    {
        if (args is ["--help"] or ["help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string[] common = ["--project", "--jobs", "--timeout-seconds", "--executable"];
        string[] metadata = ["--target", "--build-dir", "--preset", "--configuration"];
        string[] allowed = command switch
        {
            "format" => [.. common, .. metadata, "--check", "--apply", "--diff", "--batch-size",
                "--changed", "--staged", "--language", "--include", "--exclude", "--tool-policy", "--json"],
            "lint" => [.. common, .. metadata, "--tool", "--checks", "--export-fixes", "--apply-replacements"],
            "analyze" => [.. common, .. metadata],
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        List<string> paths = [];
        Dictionary<string, string?> values = DeveloperCommand.Parse(args, allowed, command == "format" ? paths : null);
        if (command == "format")
        {
            if (FormattingModes.Count(values.ContainsKey) > 1 ||
                (values.ContainsKey("--changed") && values.ContainsKey("--staged")))
            {
                throw new ArgumentException("Select one formatting mode and one file scope.");
            }

            string? target = values.GetValueOrDefault("--target");
            FormatRequest format = new(values.GetValueOrDefault("--project") ?? Directory.GetCurrentDirectory())
            {
                Languages = values.GetValueOrDefault("--language") switch
                {
                    null when target is not null || values.ContainsKey("--executable") => FormatLanguage.Cpp,
                    null when values.ContainsKey("--diff") => FormatLanguage.CMake,
                    null or "all" => FormatLanguage.All,
                    "cpp" => FormatLanguage.Cpp,
                    "cmake" => FormatLanguage.CMake,
                    _ => throw new ArgumentException("--language must be cpp, cmake or all.")
                },
                Mode = values.ContainsKey("--apply") ? FormatMode.Apply : values.ContainsKey("--diff") ? FormatMode.Diff : FormatMode.Check,
                Selection = values.ContainsKey("--changed") ? FormatSelection.Changed :
                    values.ContainsKey("--staged") ? FormatSelection.Staged : FormatSelection.Project,
                Files = paths.Count == 0 ? null : paths,
                Include = values.GetValueOrDefault("--include"),
                Exclude = values.GetValueOrDefault("--exclude"),
                ToolPolicy = values.GetValueOrDefault("--tool-policy") switch
                {
                    null or "AllowSystem" => ToolResolutionPolicy.AllowSystem,
                    "ManagedOnly" => ToolResolutionPolicy.ManagedOnly,
                    _ => throw new ArgumentException("--tool-policy must be AllowSystem or ManagedOnly.")
                },
                Executable = values.GetValueOrDefault("--executable"),
                Jobs = Number("--jobs", Math.Clamp(Environment.ProcessorCount, 1, 8), 256),
                BatchSize = Number("--batch-size", 128, 4096),
                Timeout = TimeSpan.FromSeconds(Number("--timeout-seconds", 300, 86400))
            };
            if (target is null && metadata.Skip(1).Any(values.ContainsKey))
            {
                throw new ArgumentException("Build metadata for formatting requires --target.");
            }
            FormatResult formatted = target is null ? await SourceFormatting.RunAsync(format, ct) :
                await ClangTools.FormatAsync(format, new(target, values.GetValueOrDefault("--build-dir"),
                    values.GetValueOrDefault("--preset"), values.GetValueOrDefault("--configuration")),
                    diagnostic => Console.Error.Write(diagnostic.StandardOutput + diagnostic.StandardError), ct);
            if (values.ContainsKey("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(formatted, FormatReportJson.Default.FormatResult));
            }
            else
            {
                PrintFormatting(formatted);
            }
            return formatted.ExitCode;
        }
        string? replacements = values.GetValueOrDefault("--apply-replacements");
        if (replacements is not null && values.Keys.Any(key =>
            key != "--apply-replacements" && !common.Contains(key, StringComparer.Ordinal)))
        {
            throw new ArgumentException("--apply-replacements is a separate action and does not accept analysis filters.");
        }
        ClangTool tool = command switch
        {
            "analyze" => ClangTool.Analyzer,
            _ when replacements is not null => ClangTool.ApplyReplacements,
            _ => values.GetValueOrDefault("--tool") switch
            {
                null or "clang-tidy" => ClangTool.Tidy,
                "clang-check" => ClangTool.Check,
                "iwyu" => ClangTool.IncludeWhatYouUse,
                _ => throw new ArgumentException("--tool must be clang-tidy, clang-check or iwyu.")
            }
        };

        ClangToolRequest request = new(values.GetValueOrDefault("--project") ?? Directory.GetCurrentDirectory(), tool)
        {
            Target = values.GetValueOrDefault("--target"),
            BuildDirectory = values.GetValueOrDefault("--build-dir"),
            Preset = values.GetValueOrDefault("--preset"),
            Configuration = values.GetValueOrDefault("--configuration"),
            Executable = values.GetValueOrDefault("--executable"),
            Checks = values.GetValueOrDefault("--checks"),
            FixesDirectory = replacements ?? values.GetValueOrDefault("--export-fixes"),
            Jobs = Number("--jobs", Math.Max(1, Math.Min(Environment.ProcessorCount, 8)), 256),
            Timeout = TimeSpan.FromSeconds(Number("--timeout-seconds", 300, 86400))
        };

        Lock output = new();
        ClangToolResult result = await ClangTools.RunAsync(request, diagnostic =>
        {
            lock (output)
            {
                if (diagnostic.StandardOutput.Length != 0)
                {
                    Console.Write(diagnostic.StandardOutput);
                }
                if (diagnostic.StandardError.Length != 0)
                {
                    Console.Error.Write(diagnostic.StandardError);
                }
                if (diagnostic.ExitCode != 0 || diagnostic.TimedOut)
                {
                    Console.Error.WriteLine(diagnostic.TimedOut
                        ? $"Timed out: {diagnostic.Input}" : $"Exit {diagnostic.ExitCode}: {diagnostic.Input}");
                }
            }
        }, ct);
        Console.WriteLine($"{result.Files} files, {result.Invocations} invocations, {result.FailedInvocations} failed");
        return result.FailedInvocations == 0 ? 0 : 1;

        int Number(string option, int fallback, int maximum) => values.GetValueOrDefault(option) is not { } raw
            ? fallback : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
                number >= 1 && number <= maximum ? number : throw new ArgumentException($"{option} requires an integer from 1 to {maximum}.");
    }
    private static void PrintFormatting(FormatResult result)
    {
        foreach (FormatInvocation invocation in result.Invocations)
        {
            if (invocation.Error.Length != 0)
            {
                Console.Error.WriteLine(invocation.Error);
            }
        }
        foreach (FormatFile file in result.Files.Where(file => file.Status is not (FormatStatus.Clean or FormatStatus.Skipped)))
        {
            if (!string.IsNullOrEmpty(file.Diff))
            {
                Console.Write(file.Diff);
            }
            else
            {
                Console.WriteLine($"{file.Status} {file.Path}");
            }
            if (file.Detail is { } detail)
            {
                Console.Error.WriteLine(detail);
            }
        }
        int failed = result.Invocations.Count(invocation => invocation.ExitCode is not (0 or 1));
        Console.WriteLine($"{result.Files.Count} files, {result.Invocations.Count} invocations, {failed} failed. {result.Status}");
    }
}
