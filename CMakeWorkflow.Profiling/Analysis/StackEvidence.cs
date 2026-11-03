using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

// Unweighted sampled call chains are evidence, never elapsed CPU time.
// Frames are stored root-to-leaf; perf script and tracerpt supply leaf-first.
public sealed class StackEvidence
{
    private const int MaximumDistinctStacks = 20000;
    private readonly Dictionary<string, (string[] Frames, int Count)> stacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> droppedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> selfFunctions = new(StringComparer.Ordinal);
    private int total, symbolized, fullyResolved, totalFrames, resolvedFrames;
    private int unknownLeafFrames, emptyStacks, droppedSamples, rejectedRecords, truncatedFrames;
    private bool hadText;

    public int Samples => total;

    public string AnalysisStatus => total == 0 ? hadText ? "unsupported_format" : "no_samples"
        : rejectedRecords > 0 || truncatedFrames > 0 || emptyStacks > 0 || droppedSamples > 0 ? "partial" : "complete";

    public void RecordParseQuality(bool nonempty, int rejected, int truncated)
    {
        hadText = nonempty;
        rejectedRecords = rejected;
        truncatedFrames += truncated;
    }

    public void Add(IReadOnlyList<string> leafFirst)
    {
        total++;
        if (leafFirst.Count == 0)
        {
            emptyStacks++;
            return;
        }

        truncatedFrames += Math.Max(0, leafFirst.Count - 96);
        var frames = leafFirst.Reverse().TakeLast(96).ToArray();
        totalFrames += frames.Length;
        var resolved = frames.Count(IsResolved);
        resolvedFrames += resolved;
        if (resolved > 0) symbolized++;
        if (resolved == frames.Length) fullyResolved++;
        if (!IsResolved(frames[^1])) unknownLeafFrames++;
        else if (selfFunctions.Count < MaximumDistinctStacks || selfFunctions.ContainsKey(frames[^1]))
            selfFunctions[frames[^1]] = selfFunctions.GetValueOrDefault(frames[^1]) + 1;
        var key = string.Join("\u001f", frames);
        if (stacks.TryGetValue(key, out var found)) stacks[key] = (found.Frames, found.Count + 1);
        else if (stacks.Count < MaximumDistinctStacks) stacks[key] = (frames, 1);
        else
        {
            droppedSamples++;
            if (droppedKeys.Count < MaximumDistinctStacks) droppedKeys.Add(key);
        }
    }

    private static bool IsResolved(string frame) =>
        !frame.StartsWith("[unknown]", StringComparison.Ordinal)
        && !frame.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && frame is not "??";

    public JsonObject ToJson(string backend, string captureId)
    {
        var rows = new JsonArray();
        foreach (var item in stacks.Values.OrderByDescending(x => x.Count)
                     .ThenBy(x => string.Join("\u001f", x.Frames), StringComparer.Ordinal).Take(250))
        {
            var frames = new JsonArray();
            foreach (var frame in item.Frames) frames.Add((JsonNode?)JsonValue.Create(frame));
            rows.Add((JsonNode)new JsonObject { ["count"] = item.Count, ["frames"] = frames });
        }

        var hot = new JsonArray();
        foreach (var (frame, count) in selfFunctions.OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key, StringComparer.Ordinal).Take(20))
            hot.Add((JsonNode)new JsonObject { ["frame"] = frame, ["selfSamples"] = count });
        return new JsonObject
        {
            ["schemaVersion"] = 2, ["backend"] = backend, ["captureId"] = captureId,
            ["analysisStatus"] = AnalysisStatus, ["samples"] = total,
            ["parsedSamples"] = total - emptyStacks,
            // Legacy name means at least one resolved frame, not a fully resolved stack.
            ["symbolizedSamples"] = symbolized, ["fullyResolvedSamples"] = fullyResolved,
            ["totalFrames"] = totalFrames, ["resolvedFrames"] = resolvedFrames,
            ["unresolvedFrames"] = totalFrames - resolvedFrames,
            ["unknownLeafFrames"] = unknownLeafFrames, ["emptyStackSamples"] = emptyStacks,
            ["rejectedRecords"] = rejectedRecords, ["truncatedFrames"] = truncatedFrames,
            ["droppedStackSamples"] = droppedSamples, ["droppedDistinctStacks"] = droppedKeys.Count,
            ["droppedDistinctStacksIsLowerBound"] = droppedKeys.Count == MaximumDistinctStacks,
            ["stacks"] = rows, ["hotFunctions"] = hot,
            ["measurementSemantics"] =
                "Unweighted root-to-leaf sampled call stacks; hot-function selfSamples count resolved leaf samples, not CPU time."
        };
    }
}

public static partial class PerfStackAnalyzer
{
    // This field set is supported by the local perf. cpu is omitted because
    // per-task recordings may not contain PERF_SAMPLE_CPU. Default headers
    // with an optional [cpu] remain accepted for saved traces.
    [GeneratedRegex(@"^\s*([0-9a-fA-Fx]+)\s+(.+?)\s+\(([^()]*)\)\s*$")]
    private static partial Regex FramePattern();

    // -F ip,sym,dso may put the sampled leaf on the same line as the header.
    // Do not discard it: otherwise the normalizer can lose the hot function
    // even while correctly counting the sample and its callers.
    [GeneratedRegex(
        @"^(?<comm>.+?)\s+(?<pid>\d+)(?:/(?<tid>\d+))?\s+(?:\[\s*\d+\]\s+)?(?<time>\d+(?:\.\d+)?):\s*(?:\d+\s+)?(?<event>[A-Za-z][A-Za-z0-9_.:/-]*):(?:\s+(?<inline>.*))?$")]
    private static partial Regex SampleHeader();

    public static StackEvidence Parse(TextReader reader, CancellationToken ct)
    {
        var result = new StackEvidence();
        var frames = new List<string>(32);
        var inSample = false;
        var rejected = 0;
        var truncated = 0;
        var nonempty = false;
        var lines = 0;
        string? line;

        void AppendFrame(Match frame)
        {
            var address = frame.Groups[1].Value;
            var name = frame.Groups[2].Value.Trim();
            var dso = Path.GetFileName(frame.Groups[3].Value.Trim());
            var unresolved = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                             || name is "[unknown]" or "??";
            if (frames.Count < 256)
                frames.Add(unresolved
                    ? "[unknown] 0x" + address.TrimStart('0', 'x', 'X') + " @ " + dso
                    : name + " @ " + dso);
            else truncated++;
        }

        void Finish()
        {
            if (inSample) result.Add(frames);
            frames.Clear();
            inSample = false;
        }

        while ((line = reader.ReadLine()) is not null)
        {
            if ((++lines & 8191) == 0) ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                Finish();
                continue;
            }

            if (line.StartsWith('#')) continue;
            nonempty = true;
            var header = SampleHeader().Match(line);
            if (header.Success)
            {
                Finish();
                inSample = true;
                var inline = header.Groups["inline"].Value;
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    var sampledFrame = FramePattern().Match(inline);
                    if (sampledFrame.Success) AppendFrame(sampledFrame);
                    else rejected++;
                }

                continue;
            }

            var frame = FramePattern().Match(line);
            if (inSample && frame.Success) AppendFrame(frame);
            else rejected++;
        }

        Finish();
        result.RecordParseQuality(nonempty, rejected, truncated);
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
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var arg in new[]
                 {
                     "script", "-i", trace, "-F",
                     "comm,pid,tid,time,period,event,ip,sym,dso"
                 }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start perf script.");
        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        var stderr = Task.Run(async () =>
        {
            var buffer = new char[4096];
            var saved = new StringBuilder();
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, ct)) > 0)
                if (saved.Length < 16384)
                    saved.Append(buffer, 0, Math.Min(read, 16384 - saved.Length));
            return saved.ToString();
        }, ct);
        var evidence = await Task.Run(() => Parse(process.StandardOutput, ct), ct);
        await process.WaitForExitAsync(ct);
        var errors = await stderr;
        if (process.ExitCode != 0) throw new IOException($"perf script failed ({process.ExitCode}): {errors}");
        if (evidence.AnalysisStatus == "unsupported_format")
            throw new InvalidDataException(
                "perf script produced nonempty output without compatible sample headers; raw trace is preserved.");
        await ProfileArtifactWriter.WriteTextAsync(file,
            evidence.ToJson("perf", captureId).ToJsonString() + "\n", ct);
        return file;
    }
}