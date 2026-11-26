using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Quality;

internal static class FormatBenchmark
{
    internal static int Run(string cli, string formatter, string[] args)
    {
        if (!File.Exists(cli) || !File.Exists(formatter) || !TryOptions(args, out int files, out int jobs, out int repeats))
        {
            Console.Error.WriteLine("Usage: CMakeWorkflow.IntegrationTests CLI --format-benchmark CMAKEFMT [--files N] [--jobs N] [--repeats N]");
            return 2;
        }
        cli = Path.GetFullPath(cli);
        formatter = Path.GetFullPath(formatter);
        using TempTree temp = new();
        string project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        string home = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName;
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["XDG_CONFIG_HOME"] = home
        };
        string[] sources = ["CMakeLists.txt", .. Enumerable.Range(0, files - 1).Select(
            index => FormattableString.Invariant($"module-{index:00000}.cmake"))];
        foreach (string name in sources)
        {
            File.WriteAllText(Path.Combine(project, name), "set(FORMAT_BENCHMARK value)\n");
        }
        string list = Write(Path.Combine(temp.Path, "files"), string.Join('\0', sources.Select(name => "./" + name)) + '\0');
        string jobCount = jobs.ToString(CultureInfo.InvariantCulture);
        string[] options = ["--check", "--verify", "--keep-going", "--report-format", "json", "--color", "never"];
        string[] batch = [.. options, "--parallel", jobCount, "--files-from", list];
        string[] wrapped = ["format", "--project", project, "--language", "cmake", "--check",
            "--executable", formatter, "--jobs", jobCount, "--json"];
        _ = Measure(formatter, batch);
        _ = Measure(cli, wrapped);

        double[] direct = new double[repeats];
        double[] throughCli = new double[repeats];
        double[] overhead = new double[repeats];
        for (int index = 0; index < repeats; ++index)
        {
            (direct[index], _) = Measure(formatter, batch);
            (throughCli[index], string stdout) = Measure(cli, wrapped);
            JsonObject report = J(stdout);
            Check(S(report["status"]) == "Clean" && report["files"]!.AsArray().Count == files &&
                report["invocations"]!.AsArray().Count == 2, "Expected one discovery and one formatting invocation");
            overhead[index] = throughCli[index] - direct[index];
        }

        Stopwatch timer = Stopwatch.StartNew();
        foreach (string name in sources)
        {
            _ = Measure(formatter, [.. options, "--parallel", "1", "./" + name]);
        }
        timer.Stop();
        Console.WriteLine(FormattableString.Invariant($"Files: {files}, jobs: {jobs}, paired measurements: {repeats}"));
        Console.WriteLine(FormattableString.Invariant($"Direct cmakefmt batch: {Median(direct):F4} s, 1 formatter process"));
        Console.WriteLine(FormattableString.Invariant($"CMakeWorkflow: {Median(throughCli):F4} s, 2 formatter processes"));
        Console.WriteLine(FormattableString.Invariant($"Median paired overhead including CLI startup, discovery and reporting: {Median(overhead):F4} s"));
        Console.WriteLine(FormattableString.Invariant($"Serial cmakefmt (one per file): {timer.Elapsed.TotalSeconds:F4} s, {files} formatter processes"));
        return 0;

        (double Seconds, string Stdout) Measure(string file, string[] arguments)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            CommandResult output = Command(file, arguments, environment: environment, workingDirectory: project);
            return (elapsed.Elapsed.TotalSeconds, output.StandardOutput);
        }
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }

    private static bool TryOptions(string[] args, out int files, out int jobs, out int repeats)
    {
        files = 1_000;
        jobs = 4;
        repeats = 5;
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length ||
                !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1)
            {
                return false;
            }
            switch (args[index])
            {
                case "--files": files = value; break;
                case "--jobs" when value <= 256: jobs = value; break;
                case "--repeats": repeats = value; break;
                default: return false;
            }
        }
        return true;
    }
}
