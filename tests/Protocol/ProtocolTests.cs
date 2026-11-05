using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests;
#pragma warning restore IDE0130

internal static class ProtocolTests
{
    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kill(int pid, int signal);

    internal static void Run()
    {
        using TempTree temp = new();
        string source = Path.Combine(temp.Path, "source");
        CopyFixture("workflow", source);
        string build = Path.Combine(temp.Path, "build");
        Configure(source, build);
        string manifest = Path.Combine(temp.Path, "workflow.json");

        string Document(string mode, int milliseconds = 0)
        {
            JsonObject doc = J(/*lang=json,strict*/ """
                        {"schemaVersion":2,"contexts":{"main":{"buildDirectory":"",
                          "mode":"adopt","provenancePolicy":"allowUnverified"}},"nodes":[
                          {"id":"build","kind":"build","context":"main","target":"first"},
                          {"id":"run","kind":"run","from":"build","arguments":[]}]}
                        """);
            doc["contexts"]!["main"]!["buildDirectory"] = build;
            doc["nodes"]![1]!["arguments"] = new JsonArray(mode, milliseconds.ToString(System.Globalization.CultureInfo.CurrentCulture));
            return doc.ToJsonString();
        }

        _ = Write(manifest, Document("args"));
        JsonObject plan = JsonNode.Parse(Split(["workflow", "plan", manifest, "--json"]).Stdout)!.AsObject();
        Check(S(plan["valid"]) == "true" && plan["nodes"]!.AsArray().Count == 2,
            "machine readable plan");
        JsonObject discovery = JsonNode.Parse(Split(["context", "inspect", build, "--json"]).Stdout)!.AsObject();
        Check(discovery["targets"]!.AsArray().Any(x => S(x?["name"]) == "first"),
            "machine readable target discovery");
        JsonObject resolution = JsonNode.Parse(Split([
            "target", "resolve", "--build-dir", build,
            "--target", "first", "--json"
        ]).Stdout)!.AsObject();
        Check(S(resolution["target"]) == "first" && S(resolution["executable"]).Length > 0,
            "machine readable target resolution");
        JsonObject tooling = JsonNode.Parse(Split(["tooling", "list", "--json"]).Stdout)!.AsObject();
        Check(tooling["profiles"]!.AsArray().Any(x => S(x?["id"]) == "profiling"),
            "tool provisioning profile discovery");
        JsonObject toolingPlan = JsonNode.Parse(Split([
            "tooling", "plan", "cmake", "--pm", "none",
            "--json"
        ]).Stdout)!.AsObject();
        Check(S(toolingPlan["schemaVersion"]) == "1"
              && toolingPlan["tools"]!.AsArray().Any(x => S(x?["id"]) == "cmake"),
            "tool provisioning plan JSON");
        JsonObject nativePlan = JsonNode.Parse(Split([
            "tooling", "plan", "--profile", "native",
            "--pm", "none", "--json"
        ]).Stdout)!.AsObject();
        Check(nativePlan["tools"]!.AsArray().Any(x => S(x?["id"]) == "ninja"),
            "native profile option");
        if (OperatingSystem.IsLinux())
        {
            string isolatedBin = Path.Combine(temp.Path, "isolated-bin");
            Dictionary<string, string> isolatedEnvironment = new() { ["PATH"] = isolatedBin };
            JsonObject dryPlan = JsonNode.Parse(Split([
                    "tooling", "setup", "cmake", "--pm", "none",
                    "--no-fallback", "--install-dir", isolatedBin, "--dry-run", "--json"
                ],
                environment: isolatedEnvironment).Stdout)!.AsObject();
            Check(S(dryPlan["ready"]) == "false" && !Directory.Exists(isolatedBin),
                "tooling dry run changed install directory");
            (string? Stdout, string? Stderr) = Split([
                "tooling", "setup", "cmake", "--pm", "none",
                "--no-fallback", "--install-dir", isolatedBin
            ], 1, isolatedEnvironment);
            Check(Stderr.Contains("--yes", StringComparison.Ordinal)
                  && string.IsNullOrEmpty(Stdout), "mutating setup lacked explicit authorization");
        }

        (string Directory, List<JsonNode> Events) success = Execute(manifest, Path.Combine(temp.Path, "success"), 0);
        Check(Indexed(success.Directory, "succeeded"), "completed workflow was not indexed automatically");
        Check(success.Events.Select(x => S(x["type"]))
            .SequenceEqual([
                "workflow_started", "node_started", "node_finished",
                "node_started", "node_finished", "workflow_finished"
            ]), "success event order");
        Check(success.Events[^1]["status"]!.ToString() == "succeeded", "success terminal event");
        JsonObject report = JsonNode.Parse(Split(["workflow", "report", success.Directory, "--json"]).Stdout)!.AsObject();
        Check(S(report["status"]) == "succeeded", "machine readable report");
        Check(success.Events.Select(x => (int)x["sequence"]!).SequenceEqual([1, 2, 3, 4, 5, 6]),
            "event sequence numbers");
        Check(success.Events[4]!["artifacts"]!.AsArray().Any(x => S(x?["type"]) == "executable"),
            "artifact event");
        string quietResults = Path.Combine(temp.Path, "quiet");
        (string Stdout, string Stderr) quiet = Split([
            "workflow", "run", manifest, "--results-dir", quietResults,
            "--no-history"
        ]);
        string quietDirectory = Directory.GetDirectories(quietResults).Single();
        Check(string.IsNullOrEmpty(quiet.Stdout)
              && File.ReadAllLines(Path.Combine(quietDirectory, "events.jsonl")).Length == 6
              && !Indexed(quietDirectory, "succeeded"),
            "journal persists without stdout stream or optional indexing");
        string customResults = Path.Combine(temp.Path, "custom-results");
        string customDatabase = Path.Combine(temp.Path, "custom-history.sqlite");
        _ = Split([
            "workflow", "run", manifest, "--results-dir", customResults,
            "--history-db", customDatabase
        ]);
        string customDirectory = Directory.GetDirectories(customResults).Single();
        Check(!Indexed(customDirectory, "succeeded")
              && Indexed(customDirectory, "succeeded", customDatabase),
            "explicit history database routing");
        string unavailableDatabase = Path.Combine(temp.Path, "database-is-directory");
        _ = Directory.CreateDirectory(unavailableDatabase);
        string unavailableResults = Path.Combine(temp.Path, "unavailable-history");
        (string Stdout, string Stderr) unavailable = Split([
            "workflow", "run", manifest, "--results-dir",
            unavailableResults, "--history-db", unavailableDatabase, "--json"
        ]);
        string unavailableDirectory = Directory.GetDirectories(unavailableResults).Single();
        Check(unavailable.Stderr.Contains("History indexing failed", StringComparison.Ordinal)
              && S(Parse(unavailable.Stdout)[^1]["status"]) == "succeeded"
              && S(JsonNode.Parse(File.ReadAllText(Path.Combine(unavailableDirectory,
                  "run.json")))?["status"]) == "succeeded",
            "history failure changed workflow result");
        _ = Write(manifest, Document("sleep", 500));
        string detachedResults = Path.Combine(temp.Path, "detached");
        using (Process detached = Start([
                   "workflow", "run", manifest, "--results-dir",
                   detachedResults, "--json"
               ]))
        {
            Task<string> diagnostics = detached.StandardError.ReadToEndAsync();
            Check(JsonNode.Parse(detached.StandardOutput.ReadLine()!)?["type"]?.ToString()
                  == "workflow_started", "initial detached event");
            detached.StandardOutput.Close();
            Check(detached.WaitForExit(15000) && detached.ExitCode == 0,
                "closed stdout interrupted workflow");
            _ = diagnostics.GetAwaiter().GetResult();
        }

        string detachedDirectory = Directory.GetDirectories(detachedResults).Single();
        Check(S(Parse(File.ReadAllText(Path.Combine(detachedDirectory, "events.jsonl")))[^1]["status"])
              == "succeeded", "journal did not finish after stdout disconnected");

        _ = Write(manifest, Document("fail"));
        (string Directory, List<JsonNode> Events) failed = Execute(manifest, Path.Combine(temp.Path, "failed"), 1);
        Check(Indexed(failed.Directory, "failed"), "failed workflow was not indexed automatically");
        Check(failed.Events.Any(x => S(x["type"]) == "node_finished"
                                     && S(x["nodeId"]) == "run" && S(x["status"]) == "failed"
                                     && S(x["exitCode"]) == "7"), "failure event");
        Check(S(failed.Events[^1]["status"]) == "failed", "failure terminal event");

        if (!OperatingSystem.IsWindows())
        {
            _ = Write(manifest, Document("sleep", 30000));
            string results = Path.Combine(temp.Path, "canceled");
            string ready = Path.Combine(build, "ready.pid");
            if (File.Exists(ready))
            {
                File.Delete(ready);
            }

            using Process process = Start(["workflow", "run", manifest, "--results-dir", results, "--json"]);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            DateTime until = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(ready) && !process.HasExited && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
            }

            Check(File.Exists(ready) && !process.HasExited, "cancellation fixture did not start");
            Check(kill(process.Id, 2) == 0, "SIGINT delivery");
            Check(process.WaitForExit(15000) && process.ExitCode == 130,
                "canceled workflow exit code");
            List<JsonNode> events = Parse(stdout.GetAwaiter().GetResult());
            Check(stderr.GetAwaiter().GetResult().Contains("START run", StringComparison.Ordinal), "diagnostics on stderr");
            Check(events.Any(x => S(x["nodeId"]) == "run" && S(x["status"]) == "canceled"),
                "node cancellation event");
            Check(S(events[^1]["type"]) == "workflow_finished"
                  && S(events[^1]["status"]) == "canceled", "workflow cancellation event");
            string directory = Directory.GetDirectories(results).Single();
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
        (string? Stdout, string? Stderr) = Split([
            "workflow", "run", manifest, "--results-dir", results,
            "--json"
        ], expected);
        Check(Stderr.Contains("START build", StringComparison.Ordinal), "diagnostics on stderr");
        string directory = Directory.GetDirectories(results).Single();
        List<JsonNode> events = Parse(Stdout);
        string[] persisted = File.ReadAllLines(Path.Combine(directory, "events.jsonl"));
        // Console.Out.WriteLine uses CRLF on Windows; persisted JSONL is read as
        // logical lines. Compare line contents, not platform line terminators.
        IEnumerable<string> streamed = Stdout.TrimEnd('\r', '\n').Split('\n')
            .Select(line => line.TrimEnd('\r'));
        Check(streamed.SequenceEqual(persisted),
            "stdout and persisted event stream differ");
        JsonNode report = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
        Check(S(events[^1]["status"]) == S(report["status"]), "event and journal status differ");
        return (directory, events);
    }

    private static List<JsonNode> Parse(string output) => [.. output.TrimEnd().Split('\n').Select(line => JsonNode.Parse(line)!)];

    private static bool Indexed(string directory, string status, string? database = null)
    {
        List<string> args = ["history", "runs", "--json"];
        if (database is not null)
        {
            args.AddRange(["--db", database]);
        }

        JsonArray rows = JsonNode.Parse(Split(args).Stdout)!.AsArray();
        return rows.Any(x => S(x?["directory"]) == directory && S(x?["status"]) == status);
    }

    private static Process Start(IEnumerable<string> args,
        Dictionary<string, string>? environment = null)
    {
        ProcessStartInfo info = new(Cli)
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

        return Process.Start(info) ?? throw new UnreachableException("Could not start CLI");
    }

    private static (string Stdout, string Stderr) Split(IEnumerable<string> args, int expected = 0,
        Dictionary<string, string>? environment = null)
    {
        using Process process = Start(args, environment);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Check(process.WaitForExit(90000), "CLI timed out");
        string outText = stdout.GetAwaiter().GetResult();
        string errText = stderr.GetAwaiter().GetResult();
        Check(process.ExitCode == expected, $"Unexpected exit {process.ExitCode}: {errText}");
        return (outText, errText);
    }
}