using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Profiling;

// Report semantics for DagFlow's historical stress-harness experiment.
public static class DagFlowHistoricalReport
{
    public static async Task<int> RenderAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct))
            ?.AsObject() ?? throw new InvalidDataException("Historical profile manifest is missing.");
        var snapshot = Path.Combine(directory, "source");
        foreach (var (path, hashNode) in manifest["source_sha256"]?.AsObject()
            ?? throw new InvalidDataException("Source snapshot identity is missing."))
        {
            var source = Path.GetFullPath(Path.Combine(snapshot, path));
            if (!source.StartsWith(snapshot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || await CommandExecution.Sha256Async(source, ct) != hashNode!.GetValue<string>())
                throw new InvalidDataException("Source snapshot changed: " + path);
        }
        foreach (var (name, node) in manifest["binaries"]?.AsObject()
            ?? throw new InvalidDataException("Binary identity is missing."))
        {
            var binary = node?.AsObject() ?? throw new InvalidDataException("Invalid binary: " + name);
            if (await CommandExecution.Sha256Async(binary["path"]!.GetValue<string>(), ct)
                != binary["sha256"]!.GetValue<string>())
                throw new InvalidDataException("Measured binary changed: " + name);
        }
        if (manifest["diagnostic_frame_pointers"] is JsonObject fpIdentity)
            foreach (var (name, node) in fpIdentity["binaries"]?.AsObject()
                ?? throw new InvalidDataException("Missing diagnostic frame-pointer binary identities."))
            {
                var binary = node?.AsObject() ?? throw new InvalidDataException("Invalid FP binary: " + name);
                if (await CommandExecution.Sha256Async(binary["path"]!.GetValue<string>(), ct)
                    != binary["sha256"]!.GetValue<string>())
                    throw new InvalidDataException("Diagnostic frame-pointer binary changed: " + name);
            }
        var rows = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "summary.json"), ct))
            ?.AsArray() ?? throw new InvalidDataException("Historical profile summary is missing.");
        // Two variants x two worker counts x six scenarios; never produce a
        // seemingly complete HTML report from a truncated campaign.
        if (rows.Count != 24) throw new InvalidDataException("Historical summary must contain exactly 24 profiles.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var release = new JsonObject();
        var framePointers = new JsonObject();
        var body = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>DagFlow O3 / Full LTO</title>
            <style>body{font:15px system-ui;margin:2rem;color:#19212c}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.5rem;text-align:right}td:first-child,td:nth-child(3){text-align:left}a{color:#145eb0}thead{background:#eee}</style>
            <h1>bench/stress_harness.cpp: O3 versus O3 + Full LTO</h1>
            <p>Timings and counters come from separate processes. Flamegraph widths are sums of sample periods, not elapsed time. Diagnostic frame-pointer builds are excluded from timing and counter tables. Frequency and host load are not attested.</p>
            <p><a href="summary.csv">Counters (CSV)</a> · <a href="manifest.json">Manifest</a></p>
            <table><thead><tr><th>Build</th><th>Workers</th><th>Scenario</th><th>Time, µs</th><th>Cycles, M</th><th>Instructions, M</th><th>Branches, M</th><th>Branch misses, M</th><th>Cache refs, M</th><th>Cache misses, M</th><th>Page faults</th><th>Samples</th><th>Profiles</th></tr></thead><tbody>

            """);
        foreach (var rowNode in rows)
        {
            ct.ThrowIfCancellationRequested();
            var row = rowNode?.AsObject() ?? throw new InvalidDataException("Invalid summary row.");
            var profile = row["profile"]!.GetValue<string>();
            var workers = row["workers"]!.GetValue<int>();
            var scenario = row["scenario"]!.GetValue<string>();
            if (profile is not ("o3" or "o3-lto") || workers is not (1 or 4)
                || !System.Text.RegularExpressions.Regex.IsMatch(scenario, "^[a-z-]+$"))
                throw new InvalidDataException("Unexpected historical profile identity.");
            var label = profile + "-" + workers + "-" + scenario;
            if (!identities.Add(label)) throw new InvalidDataException("Duplicate historical profile: " + label);
            var path = Path.Combine(directory, "profiles", label);
            var tree = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(path, "flamegraph.json"), ct),
                documentOptions: new JsonDocumentOptions { MaxDepth = 4096 })
                ?.AsObject() ?? throw new InvalidDataException("Missing flamegraph: " + label);
            await File.WriteAllTextAsync(Path.Combine(path, "flamegraph.svg"),
                DagFlowHistoricalFlamegraph.Svg(tree, label), ct);
            release[label] = Stats(tree);
            var fpPath = Path.Combine(directory, "profiles-fp", label);
            if (manifest["diagnostic_frame_pointers"] is not null
                && !File.Exists(Path.Combine(fpPath, "flamegraph.json")))
                throw new InvalidDataException("Missing declared FP flamegraph: " + label);
            if (File.Exists(Path.Combine(fpPath, "flamegraph.json")))
            {
                var fpTree = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fpPath, "flamegraph.json"), ct),
                    documentOptions: new JsonDocumentOptions { MaxDepth = 4096 })
                    ?.AsObject() ?? throw new InvalidDataException("Invalid FP flamegraph: " + label);
                await File.WriteAllTextAsync(Path.Combine(fpPath, "flamegraph.svg"),
                    DagFlowHistoricalFlamegraph.Svg(fpTree, label + " (diagnostic frame pointers)"), ct);
                framePointers[label] = Stats(fpTree);
            }
            body.Append("<tr><td>").Append(profile).Append("</td><td>").Append(workers)
                .Append("</td><td>").Append(scenario).Append("</td><td>")
                .Append(Number(row, "time_us", "F2", 1)).Append("</td>");
            foreach (var counter in new[] { "cycles:u", "instructions:u", "branches:u",
                "branch-misses:u", "cache-references:u", "cache-misses:u" })
                body.Append("<td>").Append(Number(row, counter, "F3", 1_000_000)).Append("</td>");
            body.Append("<td>").Append(Number(row, "page-faults", "F0", 1))
                .Append("</td><td>").Append(tree["metadata"]!["samples"]!.GetValue<long>())
                .Append("</td><td><a href=\"profiles/").Append(label)
                .Append("/flamegraph.svg\">Release flamegraph</a> · <a href=\"profiles/")
                .Append(label).Append("/callgraph.txt\">Call graph</a> · <a href=\"profiles/")
                .Append(label).Append("/hotspots.txt\">Hotspots</a>");
            if (framePointers.ContainsKey(label))
                body.Append("<br><a href=\"profiles-fp/").Append(label)
                    .Append("/flamegraph.svg\">FP flamegraph</a> · <a href=\"profiles-fp/")
                    .Append(label).Append("/callgraph.txt\">FP call graph</a>");
            body.Append("</td></tr>\n");
        }
        body.Append("</tbody></table><p>Raw perf data and weighted flamegraph JSON remain next to each SVG.</p></html>\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "sampling-summary.json"),
            release.ToJsonString() + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "sampling-summary-fp.json"),
            framePointers.ToJsonString() + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), body.ToString(), ct);
        var readme = Path.Combine(directory, "README.md");
        if (!File.Exists(readme))
            await File.WriteAllTextAsync(readme, """
                # DagFlow O3 versus Full LTO

                Open [index.html](index.html) for time, counters and sampled stacks.
                [summary.csv](summary.csv) contains exact metrics and [manifest.json](manifest.json)
                records source, binary identities and measurement settings.

                Timings and counters come from distinct processes; frame-pointer captures
                are diagnostic and do not contribute to the timing or counter summary.
                CPU frequency, co-running system load and off-CPU time are not attested.
                """, ct);
        Console.WriteLine("Rendered " + (release.Count + framePointers.Count) + " flamegraphs: "
            + Path.Combine(directory, "index.html"));
        return 0;
    }

    private static string Number(JsonObject row, string key, string format, double divisor)
    {
        var value = row[key]?.GetValue<double>() ?? throw new InvalidDataException("Missing metric: " + key);
        if (!double.IsFinite(value)) throw new InvalidDataException("Invalid metric: " + key);
        return (value / divisor).ToString(format, CultureInfo.InvariantCulture);
    }

    private static JsonObject Stats(JsonObject tree)
    {
        var self = new Dictionary<string, long>(StringComparer.Ordinal);
        var depth = 0;
        var frames = DagFlowHistoricalFlamegraph.ReadFrames(tree);
        var depths = new int[frames.Count];
        long total = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            depths[i] = frame.Parent < 0 ? 0 : checked(depths[frame.Parent] + 1);
            depth = Math.Max(depth, depths[i]);
            self[frame.Name] = checked(self.GetValueOrDefault(frame.Name) + frame.Self);
            total = checked(total + frame.Self);
        }
        if (total == 0) throw new InvalidDataException("No weighted samples.");
        return new JsonObject
        {
            ["samples"] = tree["metadata"]!["samples"]!.GetValue<long>(),
            ["sampled_cycles"] = total,
            ["missing_callchain_samples"] = tree["metadata"]!["missing_callchain_samples"]!.GetValue<long>(),
            ["max_depth"] = depth,
            ["top_self"] = new JsonArray(self.OrderByDescending(x => x.Value).Take(15)
                .Select(x => (JsonNode?)new JsonObject
                {
                    ["symbol"] = x.Key, ["sampled_cycles"] = x.Value,
                    ["pct"] = 100.0 * x.Value / total
                }).ToArray())
        };
    }
}
