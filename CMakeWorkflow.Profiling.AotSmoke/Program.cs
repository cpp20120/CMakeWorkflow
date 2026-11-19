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
    string pmcConfig = Path.Combine(directory, "pmc-config.json");
    string pmcXml = Path.Combine(directory, "pmc.events.xml");
    await File.WriteAllTextAsync(pmcConfig, """
        {"schemaVersion":1,"method":"etw-pmc-overflow","sources":[
          {"name":"TotalCycles","sourceId":19,"interval":65536,"role":"counter"}]}
        """);
    await File.WriteAllTextAsync(pmcXml, """
        <Events>
          <Event><System><Provider Guid="{ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}"/><Opcode>73</Opcode>
            <TimeCreated SystemTime="2026-11-19T00:00:00Z"/></System><EventData>
            <Data Name="SampleSource">19</Data><Data Name="NewInterval">65536</Data></EventData></Event>
          <Event><System><Provider Guid="{3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c}"/><Opcode>1</Opcode>
            <TimeCreated SystemTime="2026-11-19T00:00:01Z"/></System><EventData>
            <Data Name="ProcessId">42</Data><Data Name="TThreadId">101</Data></EventData></Event>
          <Event><System><Provider Guid="{ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}"/><Opcode>47</Opcode>
            <TimeCreated SystemTime="2026-11-19T00:00:02Z"/></System><EventData>
            <Data Name="ProfileSource">19</Data><Data Name="ThreadId">101</Data></EventData></Event>
        </Events>
        """);
    string pmcSummary = await CMakeWorkflow.Profiling.Analysis.EtwPmcAnalyzer.ParseXmlAsync(pmcXml, directory,
        "aot-pmc", pmcConfig, 42, new DateTimeOffset(2026, 11, 19, 0, 0, 1, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 19, 0, 0, 3, TimeSpan.Zero), CancellationToken.None);
    JsonObject pmc = JsonNode.Parse(await File.ReadAllTextAsync(pmcSummary))!.AsObject();
    if (pmc["counters"]![0]!["estimatedEvents"]!.GetValue<double>() != 65536)
    {
        throw new UnreachableException("AOT PMC decoding changed source, interval or target attribution.");
    }
    Console.WriteLine("Native PMC parser smoke passed: XML streaming, PID/thread attribution and JSON estimates.");

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
