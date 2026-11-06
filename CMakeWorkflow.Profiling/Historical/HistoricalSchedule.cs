using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Core.Persistence;

namespace CMakeWorkflow.Profiling.Historical;

/// <summary>One workload/configuration observation; block and pair identity do not assert statistical independence.</summary>
internal sealed record HistoricalMeasurement(string Kind, int Trial, string Profile, int Workers, string Scenario)
{
    internal string Label => $"{Kind}-{Trial}-{Profile}-{Workers}-{Scenario}";
    internal string Block => $"{Kind}-{Trial}";
    internal string Pair => $"{Block}-{Workers}-{Scenario}";
}

/// <summary>Materializes and verifies the historical adapter's measurement order, without executing commands.</summary>
/// <remarks>
/// Configure/build and diagnostic capture stages remain in the existing command dispatcher. Every scheduled
/// item runs serially after all builds, preserving resource/exclusivity semantics. Resume loads saved entries
/// and validates their complete identity/coverage; it never draws PRNG output to reconstruct future work.
/// </remarks>
internal static class HistoricalSchedule
{
    internal static readonly string[] Strategies =
        ["sequential", "shuffle-global", "random-interleaved", "randomized-blocks", "balanced-blocks"];
    internal static readonly int[] Workers = [1, 4];
    internal static readonly string[] Scenarios =
        ["external-contention", "hot-shard-skew", "local-overflow", "nested-helping", "mixed-chaos", "idle-burst"];
    internal static readonly string[] Profiles = ["o3", "o3-lto"];

    internal static uint[] Seeds(DagFlowHistoricalOptions options) => options.Seeds is null ? [20260930] : [.. options.Seeds];

    /// <summary>Rejects unknown IDs, incompatible scopes and unbounded experiments before any external probe.</summary>
    internal static void Validate(DagFlowHistoricalOptions options)
    {
        if (options.TimingTrials is < 1 or > 1000 || options.StatTrials is < 1 or > 1000 || options.Jobs < 1)
        {
            throw new ArgumentException("Trial counts must be 1..1000 and jobs must be positive.");
        }
        if (!HistoricalRandom.Algorithms.Contains(options.ShuffleAlgorithm, StringComparer.Ordinal) ||
            !Strategies.Contains(options.ScheduleStrategy, StringComparer.Ordinal) ||
            options.ShuffleScope is not ("block" or "global"))
        {
            throw new ArgumentException("Unknown shuffle algorithm, scheduling strategy or scope.");
        }
        if (options.ScheduleStrategy == "shuffle-global" && options.ShuffleScope != "global")
        {
            throw new ArgumentException("shuffle-global requires --shuffle-scope global.");
        }
        uint[] seeds = Seeds(options);
        if (seeds.Length is < 1 or > 64 || seeds.Distinct().Count() != seeds.Length ||
            (options.ShuffleAlgorithm == "dotnet" && seeds.Any(seed => seed > int.MaxValue)))
        {
            throw new ArgumentException("Supply 1..64 distinct UInt32 seeds; dotnet requires seeds <= 2147483647.");
        }
    }

    private static List<HistoricalMeasurement> Catalog(DagFlowHistoricalOptions options) =>
        [.. new[] { (Kind: "time", Count: options.TimingTrials), (Kind: "stat", Count: options.StatTrials) }
            .SelectMany(phase => Enumerable.Range(0, phase.Count).SelectMany(trial =>
                Workers.SelectMany(workers => Scenarios.SelectMany(scenario => Profiles.Select(profile =>
                    new HistoricalMeasurement(phase.Kind, trial, profile, workers, scenario))))))];

    private static JsonObject Identity(DagFlowHistoricalOptions options, string sourceIdentity) => new()
    {
        ["algorithm"] = options.ShuffleAlgorithm,
        ["seed"] = Seeds(options).Single(),
        ["strategy"] = options.ScheduleStrategy,
        ["scope"] = options.ShuffleScope,
        ["timingTrials"] = options.TimingTrials,
        ["statTrials"] = options.StatTrials,
        ["sourceIdentity"] = sourceIdentity,
        ["measurementSet"] = new JsonObject
        {
            ["workers"] = new JsonArray([.. Workers.Select(x => (JsonNode?)JsonValue.Create(x))]),
            ["scenarios"] = new JsonArray([.. Scenarios.Select(x => (JsonNode?)JsonValue.Create(x))]),
            ["configurations"] = new JsonArray([.. Profiles.Select(x => (JsonNode?)JsonValue.Create(x))]),
            ["configurationProtocol"] = "static-mimalloc-clang-O3-g-NDEBUG-lld-lto-none-or-full/v1"
        }
    };

    /// <summary>Generates one new experiment schedule; must be persisted before measurement dispatch.</summary>
    internal static JsonObject Create(DagFlowHistoricalOptions options, string sourceIdentity)
    {
        Validate(options);
        HistoricalRandom random = HistoricalRandom.Create(options.ShuffleAlgorithm, Seeds(options).Single());
        List<HistoricalMeasurement> catalog = Catalog(options);
        List<HistoricalMeasurement> ordered = [];
        if (options.ScheduleStrategy == "sequential")
        {
            ordered = catalog;
        }
        else if (options.ScheduleStrategy == "shuffle-global")
        {
            ordered = catalog;
            random.Shuffle(ordered);
        }
        else if (options.ScheduleStrategy == "random-interleaved")
        {
            // Retain the mutable point list and draw order of the archived Python protocol.
            List<(int Workers, string Scenario)> points = [.. Workers.SelectMany(w => Scenarios.Select(s => (w, s)))];
            List<HistoricalMeasurement[]> groups = [];
            foreach (IGrouping<string, HistoricalMeasurement> block in catalog.GroupBy(x => x.Block))
            {
                if (options.ShuffleScope == "block") { random.Shuffle(points); }
                foreach ((int workers, string scenario) in points)
                {
                    List<HistoricalMeasurement> pair = [.. block.Where(x => x.Workers == workers && x.Scenario == scenario)];
                    if (options.ShuffleScope == "block") { random.Shuffle(pair); }
                    groups.Add([.. pair]);
                }
            }
            if (options.ShuffleScope == "global")
            {
                random.Shuffle(groups);
                foreach (HistoricalMeasurement[] pair in groups) { random.Shuffle(pair); }
            }
            ordered = [.. groups.SelectMany(x => x)];
        }
        else
        {
            List<HistoricalMeasurement[]> blocks = [];
            List<string> balancedOrder = [.. Profiles];
            if (options.ScheduleStrategy == "balanced-blocks") { random.Shuffle(balancedOrder); }
            foreach (IGrouping<string, HistoricalMeasurement> block in catalog.GroupBy(x => x.Block))
            {
                List<HistoricalMeasurement> entries;
                if (options.ScheduleStrategy == "randomized-blocks")
                {
                    entries = [.. block];
                    random.Shuffle(entries);
                }
                else
                {
                    List<(int Workers, string Scenario)> points = [.. Workers.SelectMany(w => Scenarios.Select(s => (w, s)))];
                    random.Shuffle(points);
                    int rotation = block.First().Trial % Profiles.Length;
                    entries = [.. points.SelectMany(point => Enumerable.Range(0, Profiles.Length)
                        .Select(i => block.Single(x => x.Workers == point.Workers && x.Scenario == point.Scenario &&
                            x.Profile == balancedOrder[(i + rotation) % Profiles.Length])))];
                }
                blocks.Add([.. entries]);
            }
            if (options.ShuffleScope == "global") { random.Shuffle(blocks); }
            ordered = [.. blocks.SelectMany(x => x)];
        }
        return Encode(options, sourceIdentity, random.Version, ordered);
    }

    private static JsonObject Encode(DagFlowHistoricalOptions options, string sourceIdentity, string version,
        IEnumerable<HistoricalMeasurement> ordered) => new()
        {
            ["schemaVersion"] = 1,
            ["protocol"] = "dagflow-historical-schedule/v1",
            ["identity"] = Identity(options, sourceIdentity),
            ["prngVersion"] = version,
            ["measurementSetSha256"] = Hash(Identity(options, sourceIdentity)["measurementSet"]!.ToJsonString()),
            ["entries"] = new JsonArray([.. ordered.Select((entry, i) => (JsonNode?)new JsonObject
        {
            ["order"] = i, ["label"] = entry.Label, ["kind"] = entry.Kind, ["trial"] = entry.Trial,
            ["profile"] = entry.Profile, ["workers"] = entry.Workers, ["scenario"] = entry.Scenario,
            ["block"] = entry.Block, ["pair"] = entry.Pair
        })])
        };

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Loads only hash-bound schedule bytes; missing future order in an old interrupted archive is unsafe.</summary>
    /// <remarks>
    /// Caller holds the run lease and has validated source/affinity/trial evidence. A completed legacy journal
    /// can supply its already recorded order; that migration writes schedule.json and updates the in-memory
    /// manifest hash, which the caller must persist before execution. An incomplete legacy order is refused.
    /// No PRNG is instantiated. Recorded measurement commands must be a prefix of the saved permutation;
    /// their outcome/log verification remains the command dispatcher's responsibility.
    /// </remarks>
    internal static async Task<JsonObject> LoadAsync(DagFlowHistoricalOptions options, string sourceIdentity,
        JsonObject manifest, JsonArray commands, CancellationToken ct)
    {
        string path = Path.Combine(options.OutputDirectory, "schedule.json");
        JsonObject saved;
        if (manifest["schedule_sha256"] is null)
        {
            // Completed archives can be migrated from actual recorded dispatch order, without PRNG draws.
            if (options.ShuffleAlgorithm != "python-legacy" || Seeds(options).Single() != 20260930 ||
                options.ScheduleStrategy != "random-interleaved" || options.ShuffleScope != "block" ||
                manifest["ordering"]?.GetValue<string>() != "Python random.Random(20260930) shuffle schedule")
            {
                throw new InvalidDataException("Legacy archive has no compatible saved schedule.");
            }
            Dictionary<string, HistoricalMeasurement> catalog = Catalog(options).ToDictionary(x => x.Label, StringComparer.Ordinal);
            List<HistoricalMeasurement> observed = [];
            foreach (JsonObject command in commands.OfType<JsonObject>())
            {
                if (command["label"]?.GetValue<string>() is { } label && catalog.TryGetValue(label, out HistoricalMeasurement? item))
                {
                    if (command["returncode"]?.GetValue<int>() != 0 || command["timed_out"]?.GetValue<bool>() == true)
                    {
                        throw new InvalidDataException("Legacy measurement outcome is uncertain; no schedule can be inferred.");
                    }
                    observed.Add(item);
                }
            }
            if (observed.Count != catalog.Count || observed.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
            {
                throw new InvalidDataException("Interrupted legacy archive lacks a saved future schedule; use a new output directory.");
            }
            saved = Encode(options, sourceIdentity, "cmwf-python-mt19937-getrandbits/v1", observed);
            await SaveAsync(path, saved, ct);
            manifest["schedule_sha256"] = Hash(saved.ToJsonString() + "\n");
        }
        else
        {
            string text = await File.ReadAllTextAsync(path, ct);
            if (Hash(text) != manifest["schedule_sha256"]!.GetValue<string>())
            {
                throw new InvalidDataException("Saved historical schedule changed.");
            }
            saved = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Invalid saved schedule.");
        }
        Verify(saved, options, sourceIdentity);
        string[] expectedOrder = [.. saved["entries"]!.AsArray().Select(x => x!["label"]!.GetValue<string>())];
        string[] dispatched = [.. commands.OfType<JsonObject>().Select(x => x["label"]?.GetValue<string>())
            .OfType<string>().Where(x => x.StartsWith("time-", StringComparison.Ordinal) || x.StartsWith("stat-", StringComparison.Ordinal))];
        return !dispatched.SequenceEqual(expectedOrder.Take(dispatched.Length)) || dispatched.Length > expectedOrder.Length
            ? throw new InvalidDataException("Command journal does not match the saved measurement-order prefix.")
            : saved;
    }

    /// <summary>Checks identity and exact permutation coverage without instantiating any PRNG.</summary>
    internal static void Verify(JsonObject saved, DagFlowHistoricalOptions options, string sourceIdentity)
    {
        if (saved["schemaVersion"]?.GetValue<int>() != 1 ||
            saved["protocol"]?.GetValue<string>() != "dagflow-historical-schedule/v1" ||
            !JsonNode.DeepEquals(saved["identity"], Identity(options, sourceIdentity)) ||
            string.IsNullOrWhiteSpace(saved["prngVersion"]?.GetValue<string>()) ||
            saved["measurementSetSha256"]?.GetValue<string>() != Hash(Identity(options, sourceIdentity)["measurementSet"]!.ToJsonString()))
        {
            throw new InvalidDataException("Historical scheduling parameters or measurement set changed.");
        }
        Dictionary<string, HistoricalMeasurement> expected = Catalog(options).ToDictionary(x => x.Label, StringComparer.Ordinal);
        JsonArray entries = saved["entries"]?.AsArray() ?? throw new InvalidDataException("Missing schedule entries.");
        if (entries.Count != expected.Count) { throw new InvalidDataException("Incomplete saved schedule."); }
        for (int i = 0; i < entries.Count; i++)
        {
            JsonNode entry = entries[i] ?? throw new InvalidDataException("Null schedule entry.");
            if (entry["label"]?.GetValue<string>() is not { } label || !expected.Remove(label, out HistoricalMeasurement? item) ||
                entry["order"]?.GetValue<int>() != i || entry["kind"]?.GetValue<string>() != item.Kind ||
                entry["trial"]?.GetValue<int>() != item.Trial || entry["profile"]?.GetValue<string>() != item.Profile ||
                entry["workers"]?.GetValue<int>() != item.Workers || entry["scenario"]?.GetValue<string>() != item.Scenario ||
                entry["block"]?.GetValue<string>() != item.Block || entry["pair"]?.GetValue<string>() != item.Pair)
            {
                throw new InvalidDataException("Saved schedule is not an exact measurement permutation.");
            }
        }
    }

    /// <summary>Commits encoded schedule/index/completion bytes using the shared durable checkpoint writer.</summary>
    /// <remarks>
    /// Caller owns the output lease and directory. Cancellation/failure can leave a temporary sibling or
    /// replaced bytes without confirmed durability; dispatch must await successful completion.
    /// </remarks>
    internal static Task SaveAsync(string path, JsonObject schedule, CancellationToken ct) =>
        DurableByteWriter.WriteAsync(path, Encoding.UTF8.GetBytes(schedule.ToJsonString() + "\n"), ct);

    /// <summary>Parses all scheduling CLI options before capture can probe or mutate the output directory.</summary>
    internal static DagFlowHistoricalOptions ParseOptions(IReadOnlyDictionary<string, string> values, bool resume)
    {
        int Count(string name, int fallback) => !values.TryGetValue(name, out string? text) ? fallback
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value
            : throw new ArgumentException(name + " must be a positive integer.");
        uint[]? seeds = null;
        if (values.TryGetValue("--seeds", out string? seedText))
        {
            seeds = [.. seedText.Split(',').Select(text => uint.TryParse(text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out uint seed) ? seed : throw new ArgumentException("--seeds requires comma-separated UInt32 values."))];
        }
        string strategy = values.GetValueOrDefault("--schedule-strategy", "random-interleaved");
        DagFlowHistoricalOptions options = new(values.GetValueOrDefault("--source-dir", Directory.GetCurrentDirectory()),
            values.GetValueOrDefault("--output") ?? throw new ArgumentException("--output is required."),
            Count("--trials", 5), Count("--stat-trials", 3), Count("--jobs", 4), resume,
            values.GetValueOrDefault("--shuffle-algorithm", "python-legacy"), seeds, strategy,
            values.GetValueOrDefault("--shuffle-scope", strategy == "shuffle-global" ? "global" : "block"));
        Validate(options);
        return options;
    }
}
