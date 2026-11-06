using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests.Protocol;
#pragma warning restore IDE0130

internal static class ReportTests
{
    internal static void Run()
    {
        using TempTree temp = new();
        (string Name, JsonObject Document)[] cases =
        [
            ("success",
                J(
                    /*lang=json,strict*/
                                         """{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"succeeded","nodes":[{"id":"configure","kind":"configure","status":"succeeded","buildDirectory":"/tmp/build","requestedContext":{"mode":"managed"},"sessionAfter":{"provenance":"RecordedInvocation"}},{"id":"invoke","kind":"invoke","status":"succeeded","experimentResult":"succeeded"},{"id":"test","kind":"ctest","status":"succeeded","experimentResult":"succeeded"}]}""")),
            ("failed",
                J(
                    /*lang=json,strict*/
                                         """{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"failed","nodes":[{"id":"build","kind":"build","status":"failed","exitCode":1,"stderr":"build.stderr.log","error":"Process exited with code 1."},{"id":"run","kind":"run","status":"blocked","error":"A prerequisite did not succeed."}]}""")),
            ("canceled",
                J(
                    /*lang=json,strict*/
                                         """{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"canceled","nodes":[{"id":"run","kind":"run","status":"canceled","experimentResult":"canceled","error":"Canceled."},{"id":"later","kind":"invoke","status":"canceled"}]}""")),
            ("partial",
                J(
                    /*lang=json,strict*/
                                         """{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"failed","nodes":[{"id":"done","kind":"build","status":"succeeded","buildDirectory":"/tmp/build","requestedContext":{"mode":"adopt"},"buildVerification":{"verification":"BackendSucceeded","target":{"buildDirectory":"/tmp/build","target":"worker","configuration":"Debug"},"backendExitCode":0}},{"id":"pending","kind":"run","status":"blocked"}]}"""))
        ];
        foreach ((string? name, JsonObject? document) in cases)
        {
            string directory = Path.Combine(temp.Path, name);
            string path = Write(Path.Combine(directory, "run.json"), document.ToJsonString());
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            string[] files = Directory.GetFiles(directory);
            string output = RunCli("workflow", "report", directory);
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                  && files.SequenceEqual(Directory.GetFiles(directory)), "Report changed its input");
            Check(
                output.Contains("Workflow: benchmarks", StringComparison.Ordinal) &&
                output.Contains("Status: " + S(document["status"]).ToUpperInvariant(), StringComparison.Ordinal), name);
            if (name == "success")
            {
                Check(output.Contains("experiment result: succeeded", StringComparison.Ordinal) && !output.Contains("native build verification:", StringComparison.Ordinal),
                    name);
            }

            if (name == "failed")
            {
                Check(output.Contains("exit code: 1", StringComparison.Ordinal) && output.Contains(Path.Combine(directory, "build.stderr.log"), StringComparison.Ordinal),
                    name);
            }

            if (name == "partial")
            {
                Check(
                    output.Contains("native build verification: BackendSucceeded", StringComparison.Ordinal) &&
                    output.Contains("Freshness: Unverified", StringComparison.Ordinal), name);
            }

            if (name == "canceled")
            {
                Check(output.Contains("CANCELED", StringComparison.Ordinal), name);
            }
        }

        string corrupt = Path.Combine(temp.Path, "corrupt");
        _ = Write(Path.Combine(corrupt, "run.json"), "{");
        Check(RunCli(1, "workflow", "report", corrupt).Contains("Cannot read workflow report", StringComparison.Ordinal),
            "Corrupt report accepted");
        _ = Write(Path.Combine(corrupt, "run.json"), /*lang=json,strict*/ """{"workflow":"broken","status":"failed","nodes":[null]}""");
        Check(RunCli(1, "workflow", "report", corrupt).Contains("Cannot read workflow report", StringComparison.Ordinal),
            "Invalid node accepted");
        string missing = Path.Combine(temp.Path, "missing");
        _ = Directory.CreateDirectory(missing);
        Check(RunCli(1, "workflow", "report", missing).Contains("Cannot read workflow report", StringComparison.Ordinal),
            "Missing report accepted");
        Pass("read-only reporting: success, failure, cancellation, partial and corrupt journals");
    }
}