using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.IntegrationTests.Support;

internal static class TestHarness
{
    internal static string Cli = "";
    internal static string Root = "";
    private static int runNumber;

    internal static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new UnreachableException(message);
        }
    }

    internal static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
    internal static JsonObject Copy(JsonObject node) => (JsonObject)node.DeepClone();
    internal static string S(JsonNode? node) => node?.ToString() ?? "";

    internal static JsonObject Item(JsonObject report, string id) =>
        report["nodes"]!.AsArray().Select(n => n!.AsObject()).Single(n => S(n["id"]) == id);

    internal static string Status(JsonObject report, string id) => S(Item(report, id)["status"]);

    internal static void AllSucceeded(JsonObject report) =>
        Check(report["nodes"]!.AsArray().All(n => S(n?["status"]) == "succeeded"), report.ToJsonString());

    internal static CommandResult Command(string executable, IEnumerable<string> args,
        int expected = 0, int timeoutSeconds = 90, Dictionary<string, string>? environment = null)
    {
        ProcessStartInfo info = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach ((string? key, string? value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        using Process process = Process.Start(info) ?? throw new UnreachableException($"Could not start {executable}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            _ = process.WaitForExitAsync().Wait(TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (AggregateException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            throw new UnreachableException($"Timed out: {executable}");
        }

        string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Check(process.ExitCode == expected,
            $"Expected exit {expected}, got {process.ExitCode}: {executable} {string.Join(' ', args)}\n{output}");
        return new(process.ExitCode, output);
    }

    internal static string RunCli(params string[] args) => Command(Cli, args).Output;
    internal static string RunCli(int expected, params string[] args) => Command(Cli, args, expected).Output;

    internal static string Write(string path, string contents)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    internal static (JsonObject Report, string Directory) Workflow(string root, JsonObject document,
        int expected = 0, string? name = null, params string[] options)
    {
        name ??= "workflow-" + Interlocked.Increment(ref runNumber);
        string manifest = Write(Path.Combine(root, name + ".json"), document.ToJsonString());
        string results = Path.Combine(root, name + "-results");
        _ = Command(Cli, ["workflow", "run", manifest, "--results-dir", results, .. options], expected);
        string journal = Directory.EnumerateFiles(results, "run.json", SearchOption.AllDirectories).Single();
        return (J(File.ReadAllText(journal)), Path.GetDirectoryName(journal)!);
    }

    internal static string Plan(string root, JsonObject document, int expected = 0, params string[] options)
    {
        string manifest = Write(Path.Combine(root, "plan-" + Interlocked.Increment(ref runNumber) + ".json"),
            document.ToJsonString());
        return Command(Cli, ["workflow", "plan", manifest, .. options], expected).Output;
    }

    internal static string Log(JsonObject report, string directory, string id, string stream = "stdout") =>
        File.ReadAllText(Path.Combine(directory, S(Item(report, id)[stream])));

    internal static JsonObject Inspect(string build) => J(RunCli("context", "inspect", build));

    internal static void Configure(string source, string build, bool multi = false, bool bridge = false)
    {
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        List<string> args =
        [
            "-S", source, "-B", build, "-G",
            multi ? "Ninja Multi-Config" : "Ninja", "-DCMAKE_BUILD_TYPE=Debug"
        ];
        if (bridge)
        {
            args.Add("-DWORKFLOW_RUNNER_EXECUTABLE=" + Cli.Replace('\\', '/'));
        }

        _ = Command("cmake", args);
    }

    internal static string Fingerprint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "missing";
        }

        IEnumerable<string> entries = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks
                                                          + ":" + Convert.ToHexString(
                                                              SHA256.HashData(File.ReadAllBytes(path))));
        return string.Join('\n', entries);
    }

    internal static void CopyFixture(string name, string destination)
    {
        string source = Path.Combine(Root, "tests", "fixtures", name);
        _ = Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    internal static void Pass(string description) => Console.WriteLine("PASS " + description);
}