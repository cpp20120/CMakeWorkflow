using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Analysis;

namespace CMakeWorkflow.Profiling.Historical;

// Report semantics for DagFlow's historical stress-harness experiment.
public static class DagFlowHistoricalReport
{
    public static async Task<int> RenderAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        int count = await RenderReportAsync(directory, null, null, ct);
        Console.WriteLine("Rendered " + count + " flamegraphs: " + Path.Combine(directory, "index.html"));
        return 0;
    }

    /// <summary>Renders an existing campaign without capturing profiles or launching a profiler.</summary>
    /// <remarks>Guards run before reads and publications. Each derived file is replaced atomically.</remarks>
    public static async Task<int> RenderReportAsync(string directory, Action<string>? checkPath,
        Action<string>? checkMetadataRead, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        async Task<JsonNode> ReadJsonAsync(string path, int maxDepth = 0)
        {
            checkPath?.Invoke(path);
            checkMetadataRead?.Invoke(path);
            return JsonNode.Parse(await File.ReadAllTextAsync(path, ct),
                       documentOptions: new JsonDocumentOptions { MaxDepth = maxDepth })
                   ?? throw new InvalidDataException("Missing historical profile data: " + path);
        }

        Task WriteAsync(string path, string content)
        {
            checkPath?.Invoke(path);
            return ProfileArtifactWriter.WriteTextAsync(path, content, ct);
        }

        JsonObject manifest = (await ReadJsonAsync(Path.Combine(directory, "manifest.json"))).AsObject();
        string snapshot = Path.Combine(directory, "source");
        foreach ((string? path, JsonNode? hashNode) in manifest["source_sha256"]?.AsObject()
                                         ?? throw new InvalidDataException("Source snapshot identity is missing."))
        {
            string source = Path.GetFullPath(Path.Combine(snapshot, path));
            if (!source.StartsWith(snapshot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Source snapshot path escapes the campaign: " + path);
            }

            checkPath?.Invoke(source);
            if (await CommandExecution.Sha256Async(source, ct) != hashNode!.GetValue<string>())
            {
                throw new InvalidDataException("Source snapshot changed: " + path);
            }
        }

        foreach ((string? name, JsonNode? node) in manifest["binaries"]?.AsObject()
                                     ?? throw new InvalidDataException("Binary identity is missing."))
        {
            JsonObject binary = node?.AsObject() ?? throw new InvalidDataException("Invalid binary: " + name);
            string binaryPath = binary["path"]!.GetValue<string>();
            checkPath?.Invoke(binaryPath);
            if (await CommandExecution.Sha256Async(binaryPath, ct)
                != binary["sha256"]!.GetValue<string>())
            {
                throw new InvalidDataException("Measured binary changed: " + name);
            }
        }

        if (manifest["diagnostic_frame_pointers"] is JsonObject fpIdentity)
        {
            foreach ((string? name, JsonNode? node) in fpIdentity["binaries"]?.AsObject()
                                         ?? throw new InvalidDataException(
                                             "Missing diagnostic frame-pointer binary identities."))
            {
                JsonObject binary = node?.AsObject() ?? throw new InvalidDataException("Invalid FP binary: " + name);
                string binaryPath = binary["path"]!.GetValue<string>();
                checkPath?.Invoke(binaryPath);
                if (await CommandExecution.Sha256Async(binaryPath, ct)
                    != binary["sha256"]!.GetValue<string>())
                {
                    throw new InvalidDataException("Diagnostic frame-pointer binary changed: " + name);
                }
            }
        }

        JsonArray rows = (await ReadJsonAsync(Path.Combine(directory, "summary.json"))).AsArray();
        // Two variants x two worker counts x six scenarios; never produce a
        // seemingly complete HTML report from a truncated campaign.
        if (rows.Count != 24)
        {
            throw new InvalidDataException("Historical summary must contain exactly 24 profiles.");
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        JsonObject release = [];
        JsonObject framePointers = [];
        StringBuilder body = new("""
                                     <!doctype html><html lang="en"><meta charset="utf-8"><title>DagFlow O3 / Full LTO</title>
                                     <style>body{font:15px system-ui;margin:2rem;color:#19212c}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.5rem;text-align:right}td:first-child,td:nth-child(3){text-align:left}a{color:#145eb0}thead{background:#eee}</style>
                                     <h1>bench/stress_harness.cpp: O3 versus O3 + Full LTO</h1>
                                     <p>Timings and counters come from separate processes. Flamegraph widths are sums of sample periods, not elapsed time. Diagnostic frame-pointer builds are excluded from timing and counter tables. Frequency and host load are not attested.</p>
                                     <p><a href="summary.csv">Counters (CSV)</a> · <a href="manifest.json">Manifest</a></p>
                                     <table><thead><tr><th>Build</th><th>Workers</th><th>Scenario</th><th>Time, µs</th><th>Cycles, M</th><th>Instructions, M</th><th>Branches, M</th><th>Branch misses, M</th><th>Cache refs, M</th><th>Cache misses, M</th><th>Page faults</th><th>Samples</th><th>Profiles</th></tr></thead><tbody>

                                     """);
        foreach (JsonNode? rowNode in rows)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject row = rowNode?.AsObject() ?? throw new InvalidDataException("Invalid summary row.");
            string profile = row["profile"]!.GetValue<string>();
            int workers = row["workers"]!.GetValue<int>();
            string scenario = row["scenario"]!.GetValue<string>();
            if (profile is not ("o3" or "o3-lto") || workers is not (1 or 4)
                                                  || !System.Text.RegularExpressions.Regex.IsMatch(scenario,
                                                      "^[a-z-]+$"))
            {
                throw new InvalidDataException("Unexpected historical profile identity.");
            }

            string label = profile + "-" + workers + "-" + scenario;
            if (!identities.Add(label))
            {
                throw new InvalidDataException("Duplicate historical profile: " + label);
            }

            string path = Path.Combine(directory, "profiles", label);
            JsonObject tree = (await ReadJsonAsync(Path.Combine(path, "flamegraph.json"), 4096)).AsObject();
            await WriteAsync(Path.Combine(path, "flamegraph.svg"), DagFlowHistoricalFlamegraph.Svg(tree, label));
            release[label] = Stats(tree);
            string fpPath = Path.Combine(directory, "profiles-fp", label);
            string fpTreePath = Path.Combine(fpPath, "flamegraph.json");
            checkPath?.Invoke(fpTreePath);
            bool hasFpTree = File.Exists(fpTreePath);
            if (manifest["diagnostic_frame_pointers"] is not null && !hasFpTree)
            {
                throw new InvalidDataException("Missing declared FP flamegraph: " + label);
            }

            if (hasFpTree)
            {
                JsonObject fpTree = (await ReadJsonAsync(fpTreePath, 4096)).AsObject();
                await WriteAsync(Path.Combine(fpPath, "flamegraph.svg"),
                    DagFlowHistoricalFlamegraph.Svg(fpTree, label + " (diagnostic frame pointers)"));
                framePointers[label] = Stats(fpTree);
            }

            _ = body.Append("<tr><td>").Append(profile).Append("</td><td>").Append(workers)
                .Append("</td><td>").Append(scenario).Append("</td><td>")
                .Append(Number(row, "time_us", "F2", 1)).Append("</td>");
            foreach (string? counter in new[]
                     {
                         "cycles:u", "instructions:u", "branches:u",
                         "branch-misses:u", "cache-references:u", "cache-misses:u"
                     })
            {
                _ = body.Append("<td>").Append(Number(row, counter, "F3", 1_000_000)).Append("</td>");
            }

            _ = body.Append("<td>").Append(Number(row, "page-faults", "F0", 1))
                .Append("</td><td>").Append(tree["metadata"]!["samples"]!.GetValue<long>())
                .Append("</td><td><a href=\"profiles/").Append(label)
                .Append("/flamegraph.svg\">Release flamegraph</a> · <a href=\"profiles/")
                .Append(label).Append("/callgraph.txt\">Call graph</a> · <a href=\"profiles/")
                .Append(label).Append("/hotspots.txt\">Hotspots</a>");
            if (framePointers.ContainsKey(label))
            {
                _ = body.Append("<br><a href=\"profiles-fp/").Append(label)
                    .Append("/flamegraph.svg\">FP flamegraph</a> · <a href=\"profiles-fp/")
                    .Append(label).Append("/callgraph.txt\">FP call graph</a>");
            }

            _ = body.Append("</td></tr>\n");
        }

        _ = body.Append(
            "</tbody></table><p>Raw perf data and weighted flamegraph JSON remain next to each SVG.</p></html>\n");
        await WriteAsync(Path.Combine(directory, "sampling-summary.json"), release.ToJsonString() + "\n");
        await WriteAsync(Path.Combine(directory, "sampling-summary-fp.json"), framePointers.ToJsonString() + "\n");
        await WriteAsync(Path.Combine(directory, "index.html"), body.ToString());
        string readme = Path.Combine(directory, "README.md");
        checkPath?.Invoke(readme);
        if (!File.Exists(readme))
        {
            await WriteAsync(readme, """
                                                 # DagFlow O3 versus Full LTO

                                                 Open [index.html](index.html) for time, counters and sampled stacks.
                                                 [summary.csv](summary.csv) contains exact metrics and [manifest.json](manifest.json)
                                                 records source, binary identities and measurement settings.

                                                 Timings and counters come from distinct processes; frame-pointer captures
                                                 are diagnostic and do not contribute to the timing or counter summary.
                                                 CPU frequency, co-running system load and off-CPU time are not attested.
                                                 """);
        }

        return release.Count + framePointers.Count;
    }

    private static string Number(JsonObject row, string key, string format, double divisor)
    {
        double value = row[key]?.GetValue<double>() ?? throw new InvalidDataException("Missing metric: " + key);
        return !double.IsFinite(value)
            ? throw new InvalidDataException("Invalid metric: " + key)
            : (value / divisor).ToString(format, CultureInfo.InvariantCulture);
    }

    private static JsonObject Stats(JsonObject tree)
    {
        Dictionary<string, long> self = new(StringComparer.Ordinal);
        int depth = 0;
        IReadOnlyList<DagFlowHistoricalFlamegraph.WeightedFrame> frames = DagFlowHistoricalFlamegraph.ReadFrames(tree);
        int[] depths = new int[frames.Count];
        long total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            DagFlowHistoricalFlamegraph.WeightedFrame frame = frames[i];
            depths[i] = frame.Parent < 0 ? 0 : checked(depths[frame.Parent] + 1);
            depth = Math.Max(depth, depths[i]);
            self[frame.Name] = checked(self.GetValueOrDefault(frame.Name) + frame.Self);
            total = checked(total + frame.Self);
        }

        return total == 0
            ? throw new InvalidDataException("No weighted samples.")
            : new JsonObject
            {
                ["samples"] = tree["metadata"]!["samples"]!.GetValue<long>(),
                ["sampled_cycles"] = total,
                ["missing_callchain_samples"] = tree["metadata"]!["missing_callchain_samples"]!.GetValue<long>(),
                ["max_depth"] = depth,
                ["top_self"] = new JsonArray([.. self.OrderByDescending(x => x.Value).Take(15)
                .Select(x => (JsonNode?)new JsonObject
                {
                    ["symbol"] = x.Key,
                    ["sampled_cycles"] = x.Value,
                    ["pct"] = 100.0 * x.Value / total
                })])
            };
    }
}
