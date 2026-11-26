using System.Diagnostics;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Quality;

// The runner's apphost doubles as a child executable for tests that need controlled tool behavior.
internal static class QualityToolProbe
{
    internal const string ModeVariable = "CMWF_TEST_QUALITY_PROBE";
    internal const string MarkerVariable = "CMWF_TEST_QUALITY_MARKER";

    internal static string AppHost
    {
        get
        {
            string file = Path.GetFileNameWithoutExtension(typeof(QualityToolProbe).Assembly.Location);
            string path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? file + ".exe" : file);
            Check(File.Exists(path), $"The integration test apphost is missing: {path}");
            return path;
        }
    }

    internal static int Run(string mode, string[] args) => mode switch
    {
        "tidy" => CheckCompilationDatabase(args, tidy: true),
        "iwyu" => CheckCompilationDatabase(args, tidy: false),
        "formatter" => RunFormatter(args),
        "formatter-child" => WaitForTermination(),
        _ => throw new ArgumentException($"Unknown quality tool probe: {mode}", nameof(mode))
    };

    private static int CheckCompilationDatabase(string[] args, bool tidy)
    {
        string database = tidy
            ? args[Array.IndexOf(args, "-p") + 1]
            : args is ["-p", string directory, "-j", "2"] ? directory
                : throw new UnreachableException($"Unexpected IWYU arguments: {string.Join(' ', args)}");
        JsonArray commands = JsonNode.Parse(File.ReadAllText(Path.Combine(database, "compile_commands.json")))!.AsArray();
        Check(commands.Count == 3 && commands.All(entry => S(entry?["output"]).Replace('\\', '/')
            .Contains("/runtime.dir/", StringComparison.Ordinal)), commands.ToJsonString());

        if (tidy)
        {
            string input = args[^1];
            Check(commands.Any(entry => Path.GetFullPath(S(entry?["file"]), S(entry?["directory"])) == input),
                $"No compilation command for {input}");
            string barrier = Environment.GetEnvironmentVariable(MarkerVariable)!;
            File.WriteAllText(Path.Combine(barrier, Path.GetFileName(input)), "");
            Stopwatch elapsed = Stopwatch.StartNew();
            while (Directory.EnumerateFiles(barrier).Take(2).Count() < 2)
            {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Translation units were not dispatched in parallel");
                Thread.Sleep(30);
            }
        }
        return 0;
    }

    private static int RunFormatter(string[] args)
    {
        if (args.Contains("--list-input-files", StringComparer.Ordinal))
        {
            Console.WriteLine("./CMakeLists.txt");
            return 0;
        }

        ProcessStartInfo start = new(AppHost) { UseShellExecute = false };
        start.Environment[ModeVariable] = "formatter-child";
        using Process child = Process.Start(start) ?? throw new UnreachableException("Could not start formatter child");
        string marker = Environment.GetEnvironmentVariable(MarkerVariable)!;
        File.WriteAllText(marker + ".tmp", FormattableString.Invariant($"{Environment.ProcessId} {child.Id}"));
        File.Move(marker + ".tmp", marker);
        Check(child.WaitForExit(60_000), "Formatter child outlived the probe deadline");
        return child.ExitCode;
    }

    private static int WaitForTermination()
    {
        Thread.Sleep(TimeSpan.FromSeconds(60));
        return 0;
    }
}
