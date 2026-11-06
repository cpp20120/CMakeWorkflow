using System.Diagnostics;
using System.Text.Json.Nodes;
using CMakeWorkflow.History.Models;
using CMakeWorkflow.History.Storage;
using CMakeWorkflow.Profiling.Historical;

namespace CMakeWorkflow.Profiling.Tests;

internal static class HistoricalCaptureScheduleTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new UnreachableException(message); }
    }

    internal static async Task RunAsync(string source, string root, string completed)
    {
        JsonArray commands = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(completed, "commands.json")))!.AsArray();
        string[] labels = [.. commands.Select(x => x!["label"]!.GetValue<string>())];
        int firstMeasurement = Array.FindIndex(labels, x => x.StartsWith("time-", StringComparison.Ordinal) || x.StartsWith("stat-", StringComparison.Ordinal));
        foreach (string profile in new[] { "o3", "o3-lto" })
        {
            Check(Array.IndexOf(labels, profile + "-configure") < Array.IndexOf(labels, profile + "-build") &&
                  Array.IndexOf(labels, profile + "-build") < firstMeasurement, "Schedule broke configure/build prerequisites.");
        }
        Check(Array.FindIndex(labels, x => x.StartsWith("periodic-", StringComparison.Ordinal)) >
              Array.FindLastIndex(labels, x => x.StartsWith("time-", StringComparison.Ordinal) || x.StartsWith("stat-", StringComparison.Ordinal)),
            "Diagnostic capture started before measurement prerequisites completed.");
        using HistoryStore history = new(Path.Combine(root, "scheduled-history.sqlite"));
        _ = history.Import(completed, false);
        HistoryRun imported = history.Runs(50, null, null, null, null, null).Single();
        List<HistoryMeasurement> metrics = history.Metrics(imported.Id, null, null, null);
        Check(imported.Format == "dagflow-historical" && metrics.Count > 0 &&
              metrics.All(x => x.Workload!.Contains("pair_id", StringComparison.Ordinal) && x.Workload.Contains("prng_version", StringComparison.Ordinal)),
            "History lost schedule provenance.");
        Check(metrics.GroupBy(x => (x.Metric, x.Workload)).All(pair => pair.Count() == 2 && pair.Select(x => x.Variant).Distinct(StringComparer.Ordinal).Count() == 2),
            "History lost paired configuration identity.");

        string batch = Path.Combine(root, "historical-seeds");
        DagFlowHistoricalOptions options = new(source, batch, 1, 1, 1, ShuffleAlgorithm: "pcg64",
            Seeds: new uint[] { 42, 1337 }, ScheduleStrategy: "balanced-blocks");
        Check(await DagFlowHistoricalCapture.RunAsync(options, default) == 0, "Multi-seed capture failed.");
        string[] children = [Path.Combine(batch, "seed-000-42"), Path.Combine(batch, "seed-001-1337")];
        byte[][] schedules = [.. children.Select(x => File.ReadAllBytes(Path.Combine(x, "schedule.json")))];
        int[] counts = [.. children.Select(x => JsonNode.Parse(File.ReadAllText(Path.Combine(x, "commands.json")))!.AsArray().Count)];
        Check(await DagFlowHistoricalCapture.RunAsync(options with { Resume = true }, default) == 0, "Multi-seed restart failed.");
        for (int i = 0; i < children.Length; i++)
        {
            Check((await File.ReadAllBytesAsync(Path.Combine(children[i], "schedule.json"))).SequenceEqual(schedules[i]) &&
                  JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(children[i], "commands.json")))!.AsArray().Count == counts[i],
                "Restart regenerated order or repeated successful effects.");
        }
        _ = history.Import(batch, true);
        List<HistoryRun> runs = history.Runs(50, null, null, null, null, null);
        Check(runs.Count == 3 && runs.All(x => x.Format == "dagflow-historical"), "Seeds were pooled or imported twice.");
        foreach (HistoryRun run in runs.Where(x => x.Directory != completed))
        {
            List<HistoryMeasurement> rows = history.Metrics(run.Id, null, null, null);
            Check(rows.Select(x => JsonNode.Parse(x.Workload!)!["seed"]!.GetValue<uint>()).Distinct().Count() == 1,
                "History silently pooled different seeds.");
        }
        await Reject(() => DagFlowHistoricalCapture.RunAsync(options with { Resume = true, Seeds = new uint[] { 1337, 42 } }, default));
        await File.AppendAllTextAsync(Path.Combine(children[0], "schedule.json"), " ");
        try
        {
            _ = history.Import(batch, true);
            throw new UnreachableException("History accepted tampered saved schedule.");
        }
        catch (InvalidDataException) { }
        Check(history.Runs(50, null, null, null, null, null).Count == 3, "Rejected import partially changed history.");
        await File.WriteAllBytesAsync(Path.Combine(children[0], "schedule.json"), schedules[0]);

        string canceledOutput = Path.Combine(root, "historical-canceled");
        string marker = Path.Combine(root, "historical-cancel-effect");
        string? priorBlock = Environment.GetEnvironmentVariable("CMW_FAKE_BLOCK");
        try
        {
            Environment.SetEnvironmentVariable("CMW_FAKE_BLOCK", marker);
            using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
            DagFlowHistoricalOptions canceledOptions = new(source, canceledOutput, 1, 1, 1,
                ShuffleAlgorithm: "dotnet", Seeds: new uint[] { 42 }, ScheduleStrategy: "shuffle-global", ShuffleScope: "global");
            Task<int> execution = DagFlowHistoricalCapture.RunAsync(canceledOptions, lifetime.Token);
            while (!File.Exists(marker))
            {
                if (execution.IsCompleted) { _ = await execution; throw new UnreachableException("No active benchmark to cancel."); }
                await Task.Delay(10, lifetime.Token);
            }
            await lifetime.CancelAsync();
            try { _ = await execution; throw new UnreachableException("Canceled historical capture completed."); }
            catch (OperationCanceledException) { }
            Check(!File.Exists(Path.Combine(canceledOutput, "historical-completion.json")), "Canceled capture was finalized.");
            string effect = await File.ReadAllTextAsync(marker);
            await Reject(() => DagFlowHistoricalCapture.RunAsync(canceledOptions with { Resume = true }, default));
            Check(await File.ReadAllTextAsync(marker) == effect, "Unknown-effect benchmark was redispatched after cancellation.");
        }
        finally { Environment.SetEnvironmentVariable("CMW_FAKE_BLOCK", priorBlock); }
        Console.WriteLine("Historical execution checks passed: prerequisites, separate seeds/history pairs, saved-order restart, tamper rejection and active cancellation.");
    }

    private static async Task Reject(Func<Task<int>> action)
    {
        try { _ = await action(); }
        catch (InvalidDataException) { return; }
        throw new UnreachableException("Incompatible historical execution recovery accepted.");
    }
}