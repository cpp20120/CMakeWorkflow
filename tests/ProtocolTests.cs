using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class ProtocolTests
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);

    internal static void Run()
    {
        using var temp = new TempTree();
        var source = Path.Combine(temp.Path, "source");
        CopyFixture("workflow", source);
        var build = Path.Combine(temp.Path, "build");
        Configure(source, build);
        var manifest = Path.Combine(temp.Path, "workflow.json");
        string Document(string mode, int milliseconds = 0)
        {
            var doc = J("""
                {"schemaVersion":2,"contexts":{"main":{"buildDirectory":"",
                  "mode":"adopt","provenancePolicy":"allowUnverified"}},"nodes":[
                  {"id":"build","kind":"build","context":"main","target":"first"},
                  {"id":"run","kind":"run","from":"build","arguments":[]}]}
                """);
            doc["contexts"]!["main"]!["buildDirectory"] = build;
            doc["nodes"]![1]!["arguments"] = new JsonArray(mode, milliseconds.ToString());
            return doc.ToJsonString();
        }
        Write(manifest, Document("args"));
        var plan = JsonNode.Parse(Split(["workflow", "plan", manifest, "--json"]).Stdout)!.AsObject();
        Check(S(plan["valid"]) == "true" && plan["nodes"]!.AsArray().Count == 2,
            "machine readable plan");
        var discovery = JsonNode.Parse(Split(["context", "inspect", build, "--json"]).Stdout)!.AsObject();
        Check(discovery["targets"]!.AsArray().Any(x => S(x?["name"]) == "first"),
            "machine readable target discovery");
        var resolution = JsonNode.Parse(Split(["target", "resolve", "--build-dir", build,
            "--target", "first", "--json"]).Stdout)!.AsObject();
        Check(S(resolution["target"]) == "first" && S(resolution["executable"]).Length > 0,
            "machine readable target resolution");
        var tooling = JsonNode.Parse(Split(["tooling", "list", "--json"]).Stdout)!.AsObject();
        Check(tooling["profiles"]!.AsArray().Any(x => S(x?["id"]) == "profiling"),
            "tool provisioning profile discovery");
        var toolingPlan = JsonNode.Parse(Split(["tooling", "plan", "cmake", "--pm", "none",
            "--json"]).Stdout)!.AsObject();
        Check(S(toolingPlan["schemaVersion"]) == "1"
            && toolingPlan["tools"]!.AsArray().Any(x => S(x?["id"]) == "cmake"),
            "tool provisioning plan JSON");
        var nativePlan = JsonNode.Parse(Split(["tooling", "plan", "--profile", "native",
            "--pm", "none", "--json"]).Stdout)!.AsObject();
        Check(nativePlan["tools"]!.AsArray().Any(x => S(x?["id"]) == "ninja"),
            "native profile option");
        if (OperatingSystem.IsLinux())
        {
            var isolatedBin = Path.Combine(temp.Path, "isolated-bin");
            var isolatedEnvironment = new Dictionary<string, string> { ["PATH"] = isolatedBin };
            var dryPlan = JsonNode.Parse(Split(["tooling", "setup", "cmake", "--pm", "none",
                "--no-fallback", "--install-dir", isolatedBin, "--dry-run", "--json"],
                environment: isolatedEnvironment).Stdout)!.AsObject();
            Check(S(dryPlan["ready"]) == "false" && !Directory.Exists(isolatedBin),
                "tooling dry run changed install directory");
            var refused = Split(["tooling", "setup", "cmake", "--pm", "none",
                "--no-fallback", "--install-dir", isolatedBin], 1, isolatedEnvironment);
            Check(refused.Stderr.Contains("--yes", StringComparison.Ordinal)
                && string.IsNullOrEmpty(refused.Stdout), "mutating setup lacked explicit authorization");
        }

        var success = Execute(manifest, Path.Combine(temp.Path, "success"), 0);
        Check(Indexed(success.Directory, "succeeded"), "completed workflow was not indexed automatically");
        Check(success.Events.Select(x => S(x["type"]))
            .SequenceEqual(["workflow_started", "node_started", "node_finished",
                "node_started", "node_finished", "workflow_finished"]), "success event order");
        Check(success.Events[^1]["status"]!.ToString() == "succeeded", "success terminal event");
        var report = JsonNode.Parse(Split(["workflow", "report", success.Directory, "--json"]).Stdout)!.AsObject();
        Check(S(report["status"]) == "succeeded", "machine readable report");
        Check(success.Events.Select(x => (int)x["sequence"]!).SequenceEqual([1, 2, 3, 4, 5, 6]),
            "event sequence numbers");
        Check(success.Events[4]!["artifacts"]!.AsArray().Any(x => S(x?["type"]) == "executable"),
            "artifact event");
        var quietResults = Path.Combine(temp.Path, "quiet");
        var quiet = Split(["workflow", "run", manifest, "--results-dir", quietResults,
            "--no-history"]);
        var quietDirectory = Directory.GetDirectories(quietResults).Single();
        Check(string.IsNullOrEmpty(quiet.Stdout)
            && File.ReadAllLines(Path.Combine(quietDirectory, "events.jsonl")).Length == 6
            && !Indexed(quietDirectory, "succeeded"),
            "journal persists without stdout stream or optional indexing");
        var customResults = Path.Combine(temp.Path, "custom-results");
        var customDatabase = Path.Combine(temp.Path, "custom-history.sqlite");
        Split(["workflow", "run", manifest, "--results-dir", customResults,
            "--history-db", customDatabase]);
        var customDirectory = Directory.GetDirectories(customResults).Single();
        Check(!Indexed(customDirectory, "succeeded")
            && Indexed(customDirectory, "succeeded", customDatabase),
            "explicit history database routing");
        var unavailableDatabase = Path.Combine(temp.Path, "database-is-directory");
        Directory.CreateDirectory(unavailableDatabase);
        var unavailableResults = Path.Combine(temp.Path, "unavailable-history");
        var unavailable = Split(["workflow", "run", manifest, "--results-dir",
            unavailableResults, "--history-db", unavailableDatabase, "--json"]);
        var unavailableDirectory = Directory.GetDirectories(unavailableResults).Single();
        Check(unavailable.Stderr.Contains("History indexing failed", StringComparison.Ordinal)
            && S(Parse(unavailable.Stdout)[^1]["status"]) == "succeeded"
            && S(JsonNode.Parse(File.ReadAllText(Path.Combine(unavailableDirectory,
                "run.json")))?["status"]) == "succeeded",
            "history failure changed workflow result");
        Write(manifest, Document("sleep", 500));
        var detachedResults = Path.Combine(temp.Path, "detached");
        using (var detached = Start(["workflow", "run", manifest, "--results-dir",
            detachedResults, "--json"]))
        {
            var diagnostics = detached.StandardError.ReadToEndAsync();
            Check(JsonNode.Parse(detached.StandardOutput.ReadLine()! )?["type"]?.ToString()
                == "workflow_started", "initial detached event");
            detached.StandardOutput.Close();
            Check(detached.WaitForExit(15000) && detached.ExitCode == 0,
                "closed stdout interrupted workflow");
            _ = diagnostics.GetAwaiter().GetResult();
        }
        var detachedDirectory = Directory.GetDirectories(detachedResults).Single();
        Check(S(Parse(File.ReadAllText(Path.Combine(detachedDirectory, "events.jsonl")))[^1]["status"])
            == "succeeded", "journal did not finish after stdout disconnected");

        Write(manifest, Document("fail"));
        var failed = Execute(manifest, Path.Combine(temp.Path, "failed"), 1);
        Check(Indexed(failed.Directory, "failed"), "failed workflow was not indexed automatically");
        Check(failed.Events.Any(x => S(x["type"]) == "node_finished"
            && S(x["nodeId"]) == "run" && S(x["status"]) == "failed"
            && S(x["exitCode"]) == "7"), "failure event");
        Check(S(failed.Events[^1]["status"]) == "failed", "failure terminal event");

        if (!OperatingSystem.IsWindows())
        {
            Write(manifest, Document("sleep", 30000));
            var results = Path.Combine(temp.Path, "canceled");
            var ready = Path.Combine(build, "ready.pid");
            if (File.Exists(ready)) File.Delete(ready);
            using var process = Start(["workflow", "run", manifest, "--results-dir", results, "--json"]);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(ready) && !process.HasExited && DateTime.UtcNow < until)
                Thread.Sleep(20);
            Check(File.Exists(ready) && !process.HasExited, "cancellation fixture did not start");
            Check(kill(process.Id, 2) == 0, "SIGINT delivery");
            Check(process.WaitForExit(15000) && process.ExitCode == 130,
                "canceled workflow exit code");
            var events = Parse(stdout.GetAwaiter().GetResult());
            Check(stderr.GetAwaiter().GetResult().Contains("START run"), "diagnostics on stderr");
            Check(events.Any(x => S(x["nodeId"]) == "run" && S(x["status"]) == "canceled"),
                "node cancellation event");
            Check(S(events[^1]["type"]) == "workflow_finished"
                && S(events[^1]["status"]) == "canceled", "workflow cancellation event");
            var directory = Directory.GetDirectories(results).Single();
            Check(events.Count == File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Length,
                "cancellation stream and journal length differ");
            Check(S(JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))?["status"])
                == "canceled", "cancellation journal authority");
            Check(Indexed(directory, "canceled"), "canceled workflow was not indexed automatically");
        }
        Pass("JSON discovery, plan, report and versioned workflow events");
    }

    private static (string Directory, List<JsonNode> Events) Execute(string manifest,
        string results, int expected)
    {
        var response = Split(["workflow", "run", manifest, "--results-dir", results,
            "--json"], expected);
        Check(response.Stderr.Contains("START build", StringComparison.Ordinal), "diagnostics on stderr");
        var directory = Directory.GetDirectories(results).Single();
        var events = Parse(response.Stdout);
        var persisted = File.ReadAllLines(Path.Combine(directory, "events.jsonl"));
        // Console.Out.WriteLine uses CRLF on Windows; persisted JSONL is read as
        // logical lines. Compare line contents, not platform line terminators.
        var streamed = response.Stdout.TrimEnd('\r', '\n').Split('\n')
            .Select(line => line.TrimEnd('\r'));
        Check(streamed.SequenceEqual(persisted),
            "stdout and persisted event stream differ");
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
        Check(S(events[^1]["status"]) == S(report["status"]), "event and journal status differ");
        return (directory, events);
    }

    private static List<JsonNode> Parse(string output) => output.TrimEnd().Split('\n')
        .Select(line => JsonNode.Parse(line)!).ToList();

    private static bool Indexed(string directory, string status, string? database = null)
    {
        var args = new List<string> { "history", "runs", "--json" };
        if (database is not null) args.AddRange(["--db", database]);
        var rows = JsonNode.Parse(Split(args).Stdout)!.AsArray();
        return rows.Any(x => S(x?["directory"]) == directory && S(x?["status"]) == status);
    }

    private static Process Start(IEnumerable<string> args,
        Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(Cli) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;
        return Process.Start(info) ?? throw new Exception("Could not start CLI");
    }

    private static (string Stdout, string Stderr) Split(IEnumerable<string> args, int expected = 0,
        Dictionary<string, string>? environment = null)
    {
        using var process = Start(args, environment);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Check(process.WaitForExit(90000), "CLI timed out");
        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        Check(process.ExitCode == expected, $"Unexpected exit {process.ExitCode}: {errText}");
        return (outText, errText);
    }
}
