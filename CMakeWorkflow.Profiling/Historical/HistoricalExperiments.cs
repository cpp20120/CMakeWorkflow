using System.Globalization;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Profiling.Historical;

/// <summary>Runs seeds in distinct existing historical capture instances; never pools their measurements.</summary>
internal static class HistoricalExperiments
{
    /// <summary>Persists a seed index and runs children serially under a parent directory lease.</summary>
    /// <remarks>
    /// Each child retains its own run lease/journal and summary. Later children use the first frozen source
    /// snapshot and require its identity. A child exception aborts the batch; it never skips uncertain work or
    /// merges seed results. Cancellation can leave a durable index and an incomplete current child.
    /// </remarks>
    internal static async Task<int> RunAsync(DagFlowHistoricalOptions options, CancellationToken ct)
    {
        string output = Path.GetFullPath(options.OutputDirectory);
        _ = Directory.CreateDirectory(output);
        await using FileStream lease = new(Path.Combine(output, ".historical-capture.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        uint[] seeds = HistoricalSchedule.Seeds(options);
        JsonObject identity = new()
        {
            ["source"] = Path.GetFullPath(options.SourceDirectory),
            ["algorithm"] = options.ShuffleAlgorithm,
            ["strategy"] = options.ScheduleStrategy,
            ["scope"] = options.ShuffleScope,
            ["timingTrials"] = options.TimingTrials,
            ["statTrials"] = options.StatTrials,
            ["jobs"] = options.Jobs,
            ["seeds"] = new JsonArray([.. seeds.Select(x => (JsonNode?)JsonValue.Create(x))])
        };
        string indexPath = Path.Combine(output, "experiments.json");
        JsonObject index;
        if (options.Resume)
        {
            index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, ct))?.AsObject()
                ?? throw new InvalidDataException("Invalid seed experiment index.");
            if (index["schemaVersion"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(index["identity"], identity))
            {
                throw new InvalidDataException("Seed experiment parameters changed.");
            }
        }
        else
        {
            if (File.Exists(indexPath) || File.Exists(Path.Combine(output, "manifest.json")) || Directory.Exists(Path.Combine(output, "source")))
            {
                throw new IOException("Output already contains an experiment; use a fresh directory or --resume.");
            }
            index = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["identity"] = identity,
                ["statisticalInterpretation"] = "Seeds are ordering factors, not independent performance samples.",
                ["experiments"] = new JsonArray()
            };
            for (int i = 0; i < seeds.Length; i++)
            {
                index["experiments"]!.AsArray().Add((JsonNode)new JsonObject
                {
                    ["experiment"] = i,
                    ["seed"] = seeds[i],
                    ["directory"] = "seed-" + i.ToString("D3", CultureInfo.InvariantCulture) + "-" + seeds[i].ToString(CultureInfo.InvariantCulture)
                });
            }
            await HistoricalSchedule.SaveAsync(indexPath, index, ct);
        }
        JsonArray experiments = index["experiments"]?.AsArray() ?? throw new InvalidDataException("Missing seed experiments.");
        if (experiments.Count != seeds.Length) { throw new InvalidDataException("Incomplete seed index."); }
        for (int i = 0; i < seeds.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string name = "seed-" + i.ToString("D3", CultureInfo.InvariantCulture) + "-" + seeds[i].ToString(CultureInfo.InvariantCulture);
            JsonNode entry = experiments[i] ?? throw new InvalidDataException("Null seed experiment.");
            if (entry["seed"]?.GetValue<uint>() != seeds[i] || entry["experiment"]?.GetValue<int>() != i ||
                entry["directory"]?.GetValue<string>() != name)
            {
                throw new InvalidDataException("Seed experiment identity changed.");
            }
            string directory = Path.Combine(output, name);
            bool resume = options.Resume && File.Exists(Path.Combine(directory, "manifest.json"));
            if (options.Resume && !resume && (Directory.Exists(Path.Combine(directory, "source")) || File.Exists(Path.Combine(directory, "commands.json"))))
            {
                throw new InvalidDataException("Seed experiment has effects without a manifest; use a fresh output directory.");
            }
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Seed experiment {i + 1}/{seeds.Length}: {seeds[i]} ({directory})");
            string frozenSource = i == 0 ? options.SourceDirectory : Path.Combine(output,
                "seed-000-" + seeds[0].ToString(CultureInfo.InvariantCulture), "source");
            int code = await DagFlowHistoricalCapture.RunCoreAsync(options with
            {
                SourceDirectory = frozenSource,
                OutputDirectory = directory,
                Seeds = new[] { seeds[i] },
                Resume = resume
            }, index["sourceIdentity"]?.GetValue<string>(), ct);
            if (code != 0) { return code; }
            JsonObject childManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct))!.AsObject();
            string sourceIdentity = childManifest["scheduling"]!["identity"]!["sourceIdentity"]!.GetValue<string>();
            if (index["sourceIdentity"] is { } original && original.GetValue<string>() != sourceIdentity)
            {
                throw new InvalidDataException("Seed experiment source identity changed.");
            }
            index["sourceIdentity"] = sourceIdentity;
            await HistoricalSchedule.SaveAsync(indexPath, index, ct);
        }
        return 0;
    }
}
