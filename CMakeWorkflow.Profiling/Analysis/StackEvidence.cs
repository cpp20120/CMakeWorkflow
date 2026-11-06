using System.Text.Json.Nodes;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Analysis;
#pragma warning restore IDE0130

// Unweighted sampled call chains are evidence, never elapsed CPU time.
// Frames are stored root-to-leaf; perf script and tracerpt supply leaf-first.
public sealed class StackEvidence
{
    private const int MaximumDistinctStacks = 20000;
    private readonly Dictionary<string, (string[] Frames, int Count)> stacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> droppedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> selfFunctions = new(StringComparer.Ordinal);
    private int symbolized, fullyResolved, totalFrames, resolvedFrames;
    private int unknownLeafFrames, emptyStacks, droppedSamples, rejectedRecords, truncatedFrames;
    private bool hadText;

#pragma warning disable IDE0032 // Keep Samples getter-only for the existing reflection and serialization contract.
    private int total;
#pragma warning restore IDE0032

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
        ArgumentNullException.ThrowIfNull(leafFirst);
        total++;
        if (leafFirst.Count == 0)
        {
            emptyStacks++;
            return;
        }

        truncatedFrames += Math.Max(0, leafFirst.Count - 96);
        string[] frames = [.. leafFirst.Reverse().TakeLast(96)];
        totalFrames += frames.Length;
        int resolved = frames.Count(IsResolved);
        resolvedFrames += resolved;
        if (resolved > 0)
        {
            symbolized++;
        }

        if (resolved == frames.Length)
        {
            fullyResolved++;
        }

        if (!IsResolved(frames[^1]))
        {
            unknownLeafFrames++;
        }
        else if (selfFunctions.Count < MaximumDistinctStacks || selfFunctions.ContainsKey(frames[^1]))
        {
            selfFunctions[frames[^1]] = selfFunctions.GetValueOrDefault(frames[^1]) + 1;
        }

        string key = string.Join("\u001f", frames);
        if (stacks.TryGetValue(key, out (string[] Frames, int Count) found))
        {
            stacks[key] = (found.Frames, found.Count + 1);
        }
        else if (stacks.Count < MaximumDistinctStacks)
        {
            stacks[key] = (frames, 1);
        }
        else
        {
            droppedSamples++;
            if (droppedKeys.Count < MaximumDistinctStacks)
            {
                _ = droppedKeys.Add(key);
            }
        }
    }

    private static bool IsResolved(string frame) =>
        !frame.StartsWith("[unknown]", StringComparison.Ordinal)
        && !frame.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && frame is not "??";

    public JsonObject ToJson(string backend, string captureId)
    {
        JsonArray rows = [];
        foreach ((string[]? Frames, int Count) in stacks.Values.OrderByDescending(x => x.Count)
                     .ThenBy(x => string.Join("\u001f", x.Frames), StringComparer.Ordinal).Take(250))
        {
            JsonArray frames = [];
            foreach (string? frame in Frames)
            {
                frames.Add((JsonNode?)JsonValue.Create(frame));
            }

            rows.Add((JsonNode)new JsonObject { ["count"] = Count, ["frames"] = frames });
        }

        JsonArray hot = [];
        foreach ((string? frame, int count) in selfFunctions.OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key, StringComparer.Ordinal).Take(20))
        {
            hot.Add((JsonNode)new JsonObject { ["frame"] = frame, ["selfSamples"] = count });
        }

        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["backend"] = backend,
            ["captureId"] = captureId,
            ["analysisStatus"] = AnalysisStatus,
            ["samples"] = total,
            ["parsedSamples"] = total - emptyStacks,
            // Legacy name means at least one resolved frame, not a fully resolved stack.
            ["symbolizedSamples"] = symbolized,
            ["fullyResolvedSamples"] = fullyResolved,
            ["totalFrames"] = totalFrames,
            ["resolvedFrames"] = resolvedFrames,
            ["unresolvedFrames"] = totalFrames - resolvedFrames,
            ["unknownLeafFrames"] = unknownLeafFrames,
            ["emptyStackSamples"] = emptyStacks,
            ["rejectedRecords"] = rejectedRecords,
            ["truncatedFrames"] = truncatedFrames,
            ["droppedStackSamples"] = droppedSamples,
            ["droppedDistinctStacks"] = droppedKeys.Count,
            ["droppedDistinctStacksIsLowerBound"] = droppedKeys.Count == MaximumDistinctStacks,
            ["stacks"] = rows,
            ["hotFunctions"] = hot,
            ["measurementSemantics"] =
                "Unweighted root-to-leaf sampled call stacks; hot-function selfSamples count resolved leaf samples, not CPU time."
        };
    }
}
