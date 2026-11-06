using System.Diagnostics;
using System.Text.Json.Nodes;
using CMakeWorkflow.Profiling.Historical;

namespace CMakeWorkflow.Profiling.Tests;

internal static class HistoricalScheduleTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new UnreachableException(message); }
    }

    internal static async Task RunAsync()
    {
        HistoricalRandomGoldenVectors.Run();
        string root = Path.Combine(Path.GetTempPath(), "cmwf-schedule-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            DagFlowHistoricalOptions baseline = new(root, root, 4, 4, Seeds: new uint[] { 42 });
            JsonObject sequential = HistoricalSchedule.Create(baseline with { ScheduleStrategy = "sequential" }, "source");
            string[] labels = [.. sequential["entries"]!.AsArray().Select(x => x!["label"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
            foreach (string algorithm in HistoricalRandom.Algorithms)
            {
                foreach (string strategy in HistoricalSchedule.Strategies)
                {
                    foreach (string scope in new[] { "block", "global" })
                    {
                        if (strategy == "shuffle-global" && scope == "block") { continue; }
                        DagFlowHistoricalOptions options = baseline with
                        { ShuffleAlgorithm = algorithm, ScheduleStrategy = strategy, ShuffleScope = scope };
                        JsonObject schedule = HistoricalSchedule.Create(options, "source");
                        HistoricalSchedule.Verify(schedule, options, "source");
                        Check(JsonNode.DeepEquals(schedule, HistoricalSchedule.Create(options, "source")), "Seed/schedule nondeterminism.");
                        JsonObject[] entries = [.. schedule["entries"]!.AsArray().Select(x => x!.AsObject())];
                        Check(entries.Select(x => x["label"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(labels), "Not an exact permutation.");
                        foreach (IGrouping<string, JsonObject> block in entries.GroupBy(x => x["block"]!.GetValue<string>()))
                        {
                            Check(block.Count() == 24 && block.Select(x => x["profile"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == 2,
                                "Incomplete configuration block.");
                            Check(block.GroupBy(x => x["pair"]!.GetValue<string>()).All(pair => pair.Count() == 2), "Lost workload pairing.");
                        }
                        if (strategy == "balanced-blocks")
                        {
                            foreach (string phase in new[] { "time", "stat" })
                            {
                                foreach (IGrouping<string, JsonObject> workload in entries.Where(x => x["kind"]!.GetValue<string>() == phase)
                                             .GroupBy(x => x["workers"]!.ToJsonString() + ":" + x["scenario"]!.GetValue<string>()))
                                {
                                    string[] first = [.. workload.GroupBy(x => x["block"]!.GetValue<string>()).Select(block => block.First()["profile"]!.GetValue<string>())];
                                    Check(first.Count(x => x == "o3") == 2 && first.Count(x => x == "o3-lto") == 2, "Unbalanced configuration positions.");
                                }
                            }
                        }
                    }
                }
            }
            DagFlowHistoricalOptions legacy = new(root, root, 1, 1);
            JsonObject archived = HistoricalSchedule.Create(legacy, "source");
            string legacyLabels = string.Join("\n", archived["entries"]!.AsArray().Select(x => x!["label"]!.GetValue<string>()));
            Check(HistoricalSchedule.Hash(legacyLabels) == "545D322DFCA0C5F09FC998F986E5624F2933D73AF1F36A1A55952E8C8FEE750D", "Archived Python order changed.");

            JsonObject saved = HistoricalSchedule.Create(baseline, "source");
            saved["prngVersion"] = "saved-version/no-longer-installed";
            await HistoricalSchedule.SaveAsync(Path.Combine(root, "schedule.json"), saved, CancellationToken.None);
            JsonObject manifest = new() { ["schedule_sha256"] = HistoricalSchedule.Hash(saved.ToJsonString() + "\n") };
            JsonObject loaded = await HistoricalSchedule.LoadAsync(baseline, "source", manifest, [], CancellationToken.None);
            Check(JsonNode.DeepEquals(saved, loaded), "Resume regenerated schedule or required the old PRNG runtime.");
            JsonArray wrongPrefix = [new JsonObject { ["label"] = saved["entries"]![1]!["label"]!.DeepClone(), ["returncode"] = 0 }];
            await Reject(() => HistoricalSchedule.LoadAsync(baseline, "source", manifest, wrongPrefix, default));
            JsonObject wrongSet = saved.DeepClone().AsObject();
            wrongSet["identity"]!["measurementSet"]!["configurations"]!.AsArray().Add((JsonNode?)JsonValue.Create("extra-configuration"));
            await Reject(() => { HistoricalSchedule.Verify(wrongSet, baseline, "source"); return Task.CompletedTask; });

            await Reject(() => HistoricalSchedule.LoadAsync(baseline with { Seeds = new uint[] { 1337 } }, "source", manifest, [], default));
            await Reject(() => HistoricalSchedule.LoadAsync(baseline with { ScheduleStrategy = "balanced-blocks" }, "source", manifest, [], default));
            await Reject(() => HistoricalSchedule.LoadAsync(baseline with { TimingTrials = 5 }, "source", manifest, [], default));
            await Reject(() => HistoricalSchedule.LoadAsync(baseline, "changed-source", manifest, [], default));
            await File.AppendAllTextAsync(Path.Combine(root, "schedule.json"), " ");
            await Reject(() => HistoricalSchedule.LoadAsync(baseline, "source", manifest, [], default));
            JsonObject corrupt = saved.DeepClone().AsObject();
            corrupt["entries"]!.AsArray()[1] = corrupt["entries"]!.AsArray()[0]!.DeepClone();
            await Reject(() => { HistoricalSchedule.Verify(corrupt, baseline, "source"); return Task.CompletedTask; });
            using CancellationTokenSource canceled = new();
            await canceled.CancelAsync();
            try
            {
                await HistoricalSchedule.SaveAsync(Path.Combine(root, "canceled.json"), saved, canceled.Token);
                throw new UnreachableException("Canceled schedule was published.");
            }
            catch (OperationCanceledException) { }
            Check(!File.Exists(Path.Combine(root, "canceled.json")), "Cancellation left an authoritative schedule.");
            string archiveDirectory = Path.Combine(root, "archive");
            _ = Directory.CreateDirectory(archiveDirectory);
            DagFlowHistoricalOptions oldOptions = legacy with { OutputDirectory = archiveDirectory };
            JsonArray archiveCommands = new([.. archived["entries"]!.AsArray().Select(entry => (JsonNode?)new JsonObject
            {
                ["label"] = entry!["label"]!.DeepClone(), ["returncode"] = 0, ["timed_out"] = false
            })]);
            JsonObject oldManifest = new() { ["ordering"] = "Python random.Random(20260930) shuffle schedule" };
            JsonObject migrated = await HistoricalSchedule.LoadAsync(oldOptions, "source", oldManifest, archiveCommands, default);
            Check(JsonNode.DeepEquals(archived, migrated), "Completed archive migration regenerated a different order.");
            _ = oldManifest.Remove("schedule_sha256");
            archiveCommands.RemoveAt(archiveCommands.Count - 1);
            await Reject(() => HistoricalSchedule.LoadAsync(oldOptions, "source", oldManifest, archiveCommands, default));
            await CliValidation();
            Console.WriteLine("Historical schedule checks passed: strategies, pairs, balanced blocks, archived order, saved-order recovery, cancellation and CLI validation.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CliValidation()
    {
        string[][] invalid =
        [
            ["--shuffle-algorithm", "fake"], ["--seeds", ""], ["--seeds", "42,42"], ["--seeds", "-1"],
            ["--seeds", "4294967296"], ["--seeds", "42,,1337"], ["--schedule-strategy", "fake"],
            ["--shuffle-scope", "fake"], ["--schedule-strategy", "shuffle-global", "--shuffle-scope", "block"],
            ["--shuffle-algorithm", "dotnet", "--seeds", "4294967295"], ["--trials", "0"],
            ["--shuffle-algorithm", "pcg32", "--shuffle-algorithm", "pcg64"]
        ];
        foreach (string[] options in invalid)
        {
            try
            {
                _ = await ProfileCommand.ExecuteAsync(["run-historical", "--output", "/not-created", .. options], default);
                throw new UnreachableException("Invalid historical CLI options were accepted.");
            }
            catch (ArgumentException) { }
        }
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new UnreachableException("Incompatible historical recovery was accepted.");
    }
}