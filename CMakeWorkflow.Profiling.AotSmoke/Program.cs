using System.Diagnostics;
using System.Text.Json.Nodes;
using CMakeWorkflow.Profiling.Historical;
using CMakeWorkflow.Profiling.Tests;

HistoricalRandomGoldenVectors.Run();
string directory = Path.Combine(Path.GetTempPath(), "cmwf-schedule-aot-" + Guid.NewGuid().ToString("N"));
_ = Directory.CreateDirectory(directory);
try
{
    foreach (string algorithm in HistoricalRandom.Algorithms)
    {
        foreach (string strategy in HistoricalSchedule.Strategies)
        {
            DagFlowHistoricalOptions options = new(directory, directory, 2, 2,
                ShuffleAlgorithm: algorithm, Seeds: new uint[] { 42 }, ScheduleStrategy: strategy,
                ShuffleScope: strategy == "shuffle-global" ? "global" : "block");
            JsonObject schedule = HistoricalSchedule.Create(options, "aot-source");
            HistoricalSchedule.Verify(schedule, options, "aot-source");
            await HistoricalSchedule.SaveAsync(Path.Combine(directory, "schedule.json"), schedule, default);
            JsonObject manifest = new() { ["schedule_sha256"] = HistoricalSchedule.Hash(schedule.ToJsonString() + "\n") };
            JsonObject restored = await HistoricalSchedule.LoadAsync(options, "aot-source", manifest, [], default);
            if (!JsonNode.DeepEquals(schedule, restored)) { throw new UnreachableException("AOT schedule round-trip changed order."); }
        }
    }
    using CancellationTokenSource canceled = new();
    await canceled.CancelAsync();
    try
    {
        _ = await HistoricalExperiments.RunAsync(new(directory, Path.Combine(directory, "batch"),
            Seeds: [42, 1337]), canceled.Token);
        throw new UnreachableException("Canceled experiment batch was dispatched.");
    }
    catch (OperationCanceledException) { }
    Console.WriteLine("Native schedule smoke passed: all PRNGs/strategies, golden vectors, durable save and restored order.");
}
finally { Directory.Delete(directory, recursive: true); }
