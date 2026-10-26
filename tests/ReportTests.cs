using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class ReportTests
{
    internal static void Run()
    {
        using var temp = new TempTree();
        var cases = new (string Name, JsonObject Document)[]
        {
            ("success", J("""{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"succeeded","nodes":[{"id":"configure","kind":"configure","status":"succeeded","buildDirectory":"/tmp/build","requestedContext":{"mode":"managed"},"sessionAfter":{"provenance":"RecordedInvocation"}},{"id":"invoke","kind":"invoke","status":"succeeded","experimentResult":"succeeded"},{"id":"test","kind":"ctest","status":"succeeded","experimentResult":"succeeded"}]}""")),
            ("failed", J("""{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"failed","nodes":[{"id":"build","kind":"build","status":"failed","exitCode":1,"stderr":"build.stderr.log","error":"Process exited with code 1."},{"id":"run","kind":"run","status":"blocked","error":"A prerequisite did not succeed."}]}""")),
            ("canceled", J("""{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"canceled","nodes":[{"id":"run","kind":"run","status":"canceled","experimentResult":"canceled","error":"Canceled."},{"id":"later","kind":"invoke","status":"canceled"}]}""")),
            ("partial", J("""{"schemaVersion":1,"workflow":"benchmarks","manifestHash":"abc","status":"failed","nodes":[{"id":"done","kind":"build","status":"succeeded","buildDirectory":"/tmp/build","requestedContext":{"mode":"adopt"},"buildVerification":{"verification":"BackendSucceeded","target":{"buildDirectory":"/tmp/build","target":"worker","configuration":"Debug"},"backendExitCode":0}},{"id":"pending","kind":"run","status":"blocked"}]}"""))
        };
        foreach (var (name, document) in cases)
        {
            var directory = Path.Combine(temp.Path, name);
            var path = Write(Path.Combine(directory, "run.json"), document.ToJsonString());
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            var files = Directory.GetFiles(directory);
            var output = RunCli("workflow", "report", directory);
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                && files.SequenceEqual(Directory.GetFiles(directory)), "Report changed its input");
            Check(output.Contains("Workflow: benchmarks") && output.Contains("Status: " + S(document["status"]).ToUpperInvariant()), name);
            if (name == "success") Check(output.Contains("experiment result: succeeded") && !output.Contains("native build verification:"), name);
            if (name == "failed") Check(output.Contains("exit code: 1") && output.Contains(Path.Combine(directory, "build.stderr.log")), name);
            if (name == "partial") Check(output.Contains("native build verification: BackendSucceeded") && output.Contains("Freshness: Unverified"), name);
            if (name == "canceled") Check(output.Contains("CANCELED"), name);
        }
        var corrupt = Path.Combine(temp.Path, "corrupt");
        Write(Path.Combine(corrupt, "run.json"), "{");
        Check(RunCli(1, "workflow", "report", corrupt).Contains("Cannot read workflow report"), "Corrupt report accepted");
        Write(Path.Combine(corrupt, "run.json"), """{"workflow":"broken","status":"failed","nodes":[null]}""");
        Check(RunCli(1, "workflow", "report", corrupt).Contains("Cannot read workflow report"), "Invalid node accepted");
        var missing = Path.Combine(temp.Path, "missing");
        Directory.CreateDirectory(missing);
        Check(RunCli(1, "workflow", "report", missing).Contains("Cannot read workflow report"), "Missing report accepted");
        Pass("read-only reporting: success, failure, cancellation, partial and corrupt journals");
    }
}
