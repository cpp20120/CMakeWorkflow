using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

// Sampled call chains are evidence, not elapsed time. Counts from independent
// captures are never equated with wall-clock costs or PMU event totals.
public sealed class StackEvidence
{
    private const int MaximumDistinctStacks = 20000;
    private readonly Dictionary<string, (string[] Frames, int Count)> stacks = new(StringComparer.Ordinal);
    private int total;
    private int symbolized;
    private int droppedDistinct;

    public void Add(IReadOnlyList<string> leafFirst)
    {
        total++;
        if (leafFirst.Count == 0) return;
        var frames = leafFirst.Reverse().Take(96).ToArray();
        if (frames.Any(frame => !frame.StartsWith("[unknown]", StringComparison.Ordinal))) symbolized++;
        var key = string.Join("\u001f", frames);
        if (stacks.TryGetValue(key, out var found)) stacks[key] = (found.Frames, found.Count + 1);
        else if (stacks.Count < MaximumDistinctStacks) stacks[key] = (frames, 1);
        else droppedDistinct++;
    }

    public JsonObject ToJson(string backend, string captureId)
    {
        var rows = new JsonArray();
        foreach (var item in stacks.Values.OrderByDescending(x => x.Count).Take(250))
        {
            var frames = new JsonArray();
            foreach (var frame in item.Frames) frames.Add((JsonNode?)JsonValue.Create(frame));
            rows.Add((JsonNode)new JsonObject { ["count"] = item.Count, ["frames"] = frames });
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["backend"] = backend, ["captureId"] = captureId,
            ["samples"] = total, ["symbolizedSamples"] = symbolized,
            ["droppedDistinctStacks"] = droppedDistinct,
            ["stacks"] = rows,
            ["measurementSemantics"] = "Unweighted sampled call stacks; proportions are within-capture, never CPU time or a statistical verdict."
        };
    }
}

public static partial class PerfStackAnalyzer
{
    // perf script default callchain output: sample header, then indented
    // 'address symbol (dso)' frames. Reject unrecognized frames instead of
    // interpreting arbitrary stderr or event payloads as symbols.
    [GeneratedRegex(@"^\s*([0-9a-fA-Fx]+)\s+(.+?)\s+\(([^()]*)\)\s*$")]
    private static partial Regex FramePattern();
    [GeneratedRegex(@"\[\s*\d+\]\s+\d+\.\d+:\s+(?:\d+\s+)?(?:cycles|instructions|cpu-clock|task-clock)(?:[:/]|\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SampleHeader();

    public static StackEvidence Parse(TextReader reader, CancellationToken ct)
    {
        var result = new StackEvidence();
        var frames = new List<string>();
        var inSample = false;
        var lines = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if ((++lines & 8191) == 0) ct.ThrowIfCancellationRequested();
            if (SampleHeader().IsMatch(line))
            {
                if (inSample) result.Add(frames);
                frames.Clear(); inSample = true;
                continue;
            }
            if (!inSample) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var frame = FramePattern().Match(line);
            if (frame.Success)
            {
                var name = frame.Groups[2].Value.Trim();
                var dso = Path.GetFileName(frame.Groups[3].Value.Trim());
                var unresolved = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    || name is "[unknown]" or "??";
                frames.Add(unresolved ? "[unknown]" : name + " @ " + dso);
            }
        }
        if (inSample) result.Add(frames);
        return result;
    }

    public static async Task<string> AnalyzeAsync(string trace, string directory,
        string captureId, CancellationToken ct)
    {
        var file = Path.Combine(directory, "traces", "normalized", captureId + ".stacks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var start = new ProcessStartInfo("perf")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = directory
        };
        foreach (var arg in new[] { "script", "-i", trace }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start perf script.");
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        // Consume stderr concurrently to avoid pipe deadlock. It is bounded.
        var stderr = Task.Run(async () =>
        {
            var buffer = new char[4096]; var saved = new StringBuilder();
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, ct)) > 0)
                if (saved.Length < 16384) saved.Append(buffer, 0, Math.Min(read, 16384 - saved.Length));
            return saved.ToString();
        }, ct);
        // TextReader parsing streams stdout; no perf-script-sized string is
        // materialized in the controller process.
        var evidence = await Task.Run(() => Parse(process.StandardOutput, ct), ct);
        await process.WaitForExitAsync(ct);
        var errors = await stderr;
        if (process.ExitCode != 0) throw new IOException($"perf script failed ({process.ExitCode}): {errors}");
        await File.WriteAllTextAsync(file, evidence.ToJson("perf", captureId).ToJsonString() + "\n", ct);
        return file;
    }
}
