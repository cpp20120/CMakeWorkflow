using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.Client.Profiling;
using CMakeWorkflow.Client.Workflows;

namespace CMakeWorkflow.Client.AotSmoke;

internal static class ProfilingSmoke
{
    public static async Task RunAsync(CMakeWorkflowClient client, string fixture, CancellationToken ct)
    {
        string baseline = Path.Combine(fixture, "baseline");
        string candidate = Path.Combine(fixture, "candidate");
        await WriteReplaySourceAsync(baseline, ct);
        await WriteReplaySourceAsync(candidate, ct);
        PrepareReplayProfileOptions options = new(Guid.NewGuid().ToString("N"),
            Path.Combine(fixture, "replay"), baseline, candidate, "timing");
        PreparedWorkflow prepared = await client.Profiles.PrepareReplayAsync(options, ct);
        JsonElement plan = await client.Workflows.PlanAsync(prepared.Manifest, ct);
        if (!plan.GetProperty("valid").GetBoolean()
            || !plan.GetProperty("nodes").EnumerateArray().All(n => n.GetProperty("kind").GetString() == "profile_snapshot")
            || prepared != await client.Profiles.PrepareReplayAsync(options, ct))
        {
            throw new InvalidOperationException("Invalid native SDK replay preparation.");
        }

        string archive = Directory.CreateDirectory(Path.Combine(fixture, "historical")).FullName;
        await WriteHistoricalArchiveAsync(archive, ct);
        RenderedHistoricalProfile report = await client.Profiles.RenderHistoricalAsync(new(archive), ct);
        if (report.SchemaVersion != 1 || report.FlamegraphCount != 24
            || !File.Exists(report.IndexFile) || !File.Exists(report.SamplingSummaryFile)
            || !File.Exists(report.FramePointerSummaryFile)
            || Directory.GetFiles(archive, "flamegraph.svg", SearchOption.AllDirectories).Length != 24)
        {
            throw new InvalidOperationException("Invalid native SDK historical render response.");
        }

        Console.WriteLine("PASS NativeAOT profiling: durable replay preparation and historical rendering.");
    }

    private static async Task WriteReplaySourceAsync(string directory, CancellationToken ct)
    {
        _ = Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "binary");
        byte[] payload = "preserved benchmark fixture"u8.ToArray();
        await File.WriteAllBytesAsync(executable, payload, ct);
        JsonObject report = new()
        {
            ["status"] = "succeeded",
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["id"] = "timing",
                ["kind"] = "run",
                ["status"] = "succeeded",
                ["arguments"] = new JsonArray("--case", "smoke"),
                ["workingDirectory"] = directory,
                ["environmentOverrideHash"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                ["artifact"] = new JsonObject
                {
                    ["executable"] = executable,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(payload))
                }
            })
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "run.json"), report.ToJsonString(), ct);
    }

    private static async Task WriteHistoricalArchiveAsync(string directory, CancellationToken ct)
    {
        string binary = Path.Combine(directory, "binary");
        byte[] payload = "historical benchmark fixture"u8.ToArray();
        await File.WriteAllBytesAsync(binary, payload, ct);
        JsonObject manifest = new()
        {
            ["source_sha256"] = new JsonObject(),
            ["binaries"] = new JsonObject
            {
                ["o3"] = new JsonObject { ["path"] = binary, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(payload)) }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), manifest.ToJsonString(), ct);
        JsonArray rows = [];
        foreach (string profile in new[] { "o3", "o3-lto" })
        {
            foreach (int workers in new[] { 1, 4 })
            {
                foreach (string scenario in new[] { "a", "b", "c", "d", "e", "f" })
                {
                    JsonObject row = new() { ["profile"] = profile, ["workers"] = workers, ["scenario"] = scenario };
                    foreach (string metric in new[] { "time_us", "cycles:u", "instructions:u", "branches:u",
                                 "branch-misses:u", "cache-references:u", "cache-misses:u", "page-faults" })
                    {
                        row[metric] = 1;
                    }

                    rows.Add((JsonNode)row);
                    string profileDirectory = Directory.CreateDirectory(Path.Combine(directory, "profiles",
                        FormattableString.Invariant($"{profile}-{workers}-{scenario}"))).FullName;
                    await File.WriteAllTextAsync(Path.Combine(profileDirectory, "flamegraph.json"), """
                        {"format":"flat-prefix-tree-v1","nodes":[{"p":-1,"n":"root","v":1}],
                         "metadata":{"samples":1,"missing_callchain_samples":0}}
                        """, ct);
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), rows.ToJsonString(), ct);
    }
}
