using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Analysis;
using CMakeWorkflow.Profiling.Backends;
using CMakeWorkflow.Profiling.Capture;
using CMakeWorkflow.Profiling.Workflows;
using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Profiling.Tests;

internal static class EtwPmcTests
{
    internal static async Task RunAsync(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "pmc")).FullName;
        Recorder recorder = new();
        ProfileRequest request = new("target.exe", [], directory, new Dictionary<string, string>(), directory,
            "counters_main", "counters", TimeSpan.FromSeconds(30), MemoryStallEvent: "MemoryStalls");
        ProfileResult result = await new WindowsEtwBackend(recorder).CaptureAsync(request, CancellationToken.None);
        string configuration = result.CounterConfiguration ?? throw new UnreachableException("Missing PMC configuration.");
        XDocument profile = XDocument.Load(Path.Combine(Path.GetDirectoryName(configuration)!, "pmc.wprp"));
        Check(profile.Descendants("SampledCounter").Count() == 3
            && profile.Descendants("HardwareCounter").Single().Attribute("Strict")!.Value == "true",
            "strict overflow profile includes selected memory event");
        Check(recorder.Calls.Single(c => c.Executable == "wpr" && c.Arguments[0] == "-start").Arguments[1].EndsWith("!CMakeWorkflowPMC", StringComparison.Ordinal)
            && result.TargetProcessId == 42, "WPR custom profile and target identity");
        foreach (ProfileRequest invalid in new[]
                 {
                     request with { PmcEvents = "Unavailable" }, request with { PmcInterval = 4095 },
                     request with { PmcInterval = int.MaxValue }, request with { PmcEvents = "TotalCycles,TotalCycles" },
                     request with { MemoryStallEvent = "invalid:u" }
                 })
        {
            Recorder failed = new();
            try
            {
                _ = await new WindowsEtwBackend(failed).CaptureAsync(invalid, CancellationToken.None);
                throw new UnreachableException("Invalid PMC capture was admitted.");
            }
            catch (ArgumentException)
            {
                Check(failed.Calls.All(c => c.Executable != "target.exe" && c.Arguments[0] != "-start"),
                    "unsupported PMC rejected before recording and target launch");
            }
        }
        Recorder canceled = new() { CancelStart = true };
        try
        {
            _ = await new WindowsEtwBackend(canceled).CaptureAsync(request, CancellationToken.None);
            throw new UnreachableException("Canceled start accepted.");
        }
        catch (OperationCanceledException)
        {
            string[] start = canceled.Calls.Single(c => c.Executable == "wpr" && c.Arguments[0] == "-start").Arguments;
            string[] cancel = canceled.Calls.Single(c => c.Executable == "wpr" && c.Arguments[0] == "-cancel").Arguments;
            Check(start[^1] == cancel[^1] && canceled.Calls.All(c => c.Executable != "target.exe"), "cancel owns only its WPR session");
        }

        DateTimeOffset epoch = DateTimeOffset.Parse("2026-11-19T00:00:00Z", CultureInfo.InvariantCulture);
        const string perf = "ce1dbfb4-137e-4da6-87b0-3f59aa102cbc";
        const string thread = "3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c";
        XElement Event(string provider, int opcode, int seconds, params (string Name, int Value)[] data) => new("Event",
            new XElement("System", new XElement("Provider", new XAttribute("Guid", "{" + provider + "}")),
                new XElement("Opcode", opcode), new XElement("TimeCreated", new XAttribute("SystemTime", epoch.AddSeconds(seconds).ToString("O")))),
            new XElement("EventData", data.Select(d => new XElement("Data", new XAttribute("Name", d.Name), d.Value))));
        XElement[] events =
        [
            Event(perf, 73, 0, ("SampleSource", 19), ("NewInterval", 65536)),
            Event(perf, 73, 0, ("SampleSource", 26), ("NewInterval", 65536)),
            Event(perf, 73, 0, ("SampleSource", 40), ("NewInterval", 65536)),
            Event(thread, 1, 1, ("ProcessId", 42), ("TThreadId", 101)),
            Event(perf, 47, 2, ("ProfileSource", 19), ("ThreadId", 101)),
            Event(perf, 47, 3, ("ProfileSource", 40), ("ThreadId", 101)),
            Event(perf, 47, 4, ("ProfileSource", 40), ("ThreadId", 101)),
            Event(thread, 2, 5, ("ProcessId", 42), ("TThreadId", 101)),
            Event(thread, 1, 6, ("ProcessId", 777), ("TThreadId", 101)),
            Event(perf, 47, 7, ("ProfileSource", 40), ("ThreadId", 101)),
            Event(perf, 47, 8, ("ProfileSource", 999), ("ThreadId", 101)),
            Event(thread, 1, 9, ("ProcessId", 42), ("TThreadId", 102)),
            Event(perf, 47, 12, ("ProfileSource", 40), ("ThreadId", 102))
        ];
        string xml = Path.Combine(directory, "events.xml");
        async Task<JsonObject> ParseAsync(IEnumerable<XElement> records)
        {
            await File.WriteAllTextAsync(xml, new XDocument(new XElement("Events", records)).ToString());
            string path = await EtwPmcAnalyzer.ParseXmlAsync(xml, directory, request.CaptureId, configuration,
                42, epoch.AddSeconds(1), epoch.AddSeconds(10), CancellationToken.None);
            return JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        }
        JsonObject summary = await ParseAsync(events);
        JsonObject stalls = summary["counters"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "MemoryStalls")!.AsObject();
        Check(stalls["samples"]!.GetValue<long>() == 2 && stalls["estimatedEvents"]!.GetValue<double>() == 131072,
            "PMC attribution excludes reused threads, unknown sources and samples outside target window");
        Check(summary["counters"]![1]!["estimatedEvents"] is null, "no samples remains unavailable, not zero events");
        summary = await ParseAsync(new[] { Event(perf, 72, -1, ("SampleSource", 40), ("NewInterval", 8192)) }.Concat(events));
        Check(summary["counters"]![2]!["estimatedEvents"]!.GetValue<double>() == 131072,
            "interval setup before target launch does not invalidate subsequent matching activation");
        XElement changed = Event(perf, 72, 4, ("SampleSource", 40), ("NewInterval", 8192));
        summary = await ParseAsync(events.Take(7).Append(changed).Concat(events.Skip(7)));
        Check(summary["counters"]![2]!["estimatedEvents"] is null, "interval changes suppress estimates");
        summary = await ParseAsync(events.Where(e => e.Element("System")!.Element("Opcode")!.Value != "73"));
        Check(summary["counters"]!.AsArray().All(n => n!["estimatedEvents"] is null), "missing activation does not fabricate interval evidence");
        summary = await ParseAsync(events.Reverse());
        Check(!summary["orderedEvents"]!.GetValue<bool>() && summary["counters"]![2]!["estimatedEvents"] is null,
            "out-of-order thread evidence cannot produce estimates");
        _ = await ParseAsync(events);

        // Real tracerpt uses the shared MSNT_SystemTrace provider GUID and
        // exposes the specific classic event GUID in ExtendedTracingInfo.
        XElement RealEvent(string eventGuid, int opcode, int seconds, params (string Name, int Value)[] payload) =>
            new("Event",
                new XElement("System",
                    new XElement("Provider", new XAttribute("Guid", "{9e814aad-3204-11d2-9a82-006008a86939}")),
                    new XElement("Opcode", opcode),
                    new XElement("TimeCreated", new XAttribute("SystemTime", epoch.AddSeconds(seconds).ToString("O")))),
                new XElement("EventData", payload.Select(d => new XElement("Data", new XAttribute("Name", d.Name), d.Value))),
                new XElement("ExtendedTracingInfo", new XElement("EventGuid", "{" + eventGuid + "}")));
        JsonObject realTrace = await ParseAsync([
            RealEvent(perf, 73, 0, ("SampleSource", 19), ("NewInterval", 65536)),
            RealEvent(thread, 3, 1, ("ProcessId", 42), ("TThreadId", 101)),
            RealEvent(perf, 47, 2, ("ProfileSource", 19), ("ThreadId", 101))
        ]);
        JsonObject realCycles = realTrace["counters"]!.AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == "TotalCycles")!.AsObject();
        Check(realCycles["samples"]!.GetValue<long>() == 1
              && realCycles["estimatedEvents"]!.GetValue<double>() == 65536,
            "Real tracerpt ExtendedTracingInfo.EventGuid PMC attribution failed.");
        _ = await ParseAsync(events);

        await new ProfileSession(directory, request.CaptureId).CompleteAsync(result with
        {
            TargetStartedAt = epoch.AddSeconds(1),
            TargetFinishedAt = epoch.AddSeconds(10)
        }, CancellationToken.None);
        string database = Path.Combine(directory, "history.sqlite");
        using (CMakeWorkflow.History.Storage.HistoryStore store = new(database))
        {
            _ = await store.ImportAsync(directory, false);
            string run = store.Runs(1, null, null, null, null, null).Single().Id;
            List<CMakeWorkflow.History.Models.HistoryMeasurement> metrics = store.Metrics(run, null, null, null);
            Check(metrics.Single(m => m.Metric == "memory_stalls:MemoryStalls") is
            { Value: 131072, Unit: "estimated events" }, "History imports PMC estimates with separate units");
            Check(metrics.Count(m => m.Metric.StartsWith("pmc_samples:", StringComparison.Ordinal)) == 3,
                "History preserves observed samples separately from estimates");
            Check((await store.ImportAsync(directory, false)).Contains("unchanged 1", StringComparison.Ordinal), "PMC reimport is idempotent");
            JsonObject host = await SummaryAsync(database, run);
            Check(host["memoryStalls"]!.GetValue<double>() == 131072
                && host["memoryStallUnit"]!.GetValue<string>() == "estimated events", "host summary preserves PMC units");
            string original = await File.ReadAllTextAsync(configuration);
            await File.AppendAllTextAsync(configuration, "\n");
            try
            {
                _ = await store.ImportAsync(directory, false);
                throw new UnreachableException("Changed PMC configuration accepted by History.");
            }
            catch (InvalidDataException) { }
            await File.WriteAllTextAsync(configuration, original);
            await File.WriteAllTextAsync(Path.Combine(directory, "analysis.json"), """
                [{"captureId":"perf_main","backend":"perf","mode":"counters","counters":{
                  "cycle_activity.stalls_mem_any:u":{"value":123,"unit":""}}}]
                """);
            _ = await store.ImportAsync(directory, false);
            host = await SummaryAsync(database, run);
            Check(host["memoryStalls"] is null && host["memoryStallEvent"]!.GetValue<string>() == "mixed",
                "host summary refuses to pool perf cycles with ETW estimated events");
        }

        ProfileRecipeV1 recipe = new("profile", 1, [new ProfileVariant("main", directory)], "target", "Release", 1,
            30, [], "etw", "counters", "windows", PmcEvents: "TotalCycles,MemoryStalls", PmcInterval: 65536,
            MemoryStallEvent: "MemoryStalls");
        CMakeWorkflow.Contracts.Workflows.WorkflowDocument workflow = ProfileRecipeBuilder.Build(recipe);
        Check(workflow.Nodes.Single(n => n.Kind == "profile").Options["pmcEvents"] == recipe.PmcEvents,
            "typed Windows recipe preserves PMC configuration");
        Console.WriteLine("PASS ETW PMC discovery, strict WPR profile, cancellation ownership and PID/thread/interval attribution");
    }

    private static async Task<JsonObject> SummaryAsync(string database, string run)
    {
        TextWriter previous = Console.Out;
        using StringWriter output = new(CultureInfo.InvariantCulture);
        Console.SetOut(output);
        try
        {
            _ = await CMakeWorkflow.History.HistoryCommand.ExecuteAsync(["host-summary", run, "--db", database, "--json"], CancellationToken.None);
            return JsonNode.Parse(output.ToString())!.AsArray()[0]!.AsObject();
        }
        finally { Console.SetOut(previous); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new UnreachableException(message); }
    }

    private sealed class Recorder : IProfileProcess
    {
        internal List<(string Executable, string[] Arguments)> Calls { get; } = [];
        internal bool CancelStart { get; init; }
        public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken)
        {
            Calls.Add((executable, [.. arguments]));
            if (executable == "wpr" && arguments[0] == "-pmcsources")
            {
                return new(0, "Id Name Interval Min Max Logger\n19 TotalCycles 65536 4096 1000000\n26 InstructionRetired 65536 4096 1000000\n40 MemoryStalls 65536 4096 1000000\n", false);
            }
            if (executable == "wpr" && arguments[0] == "-start" && CancelStart) { throw new OperationCanceledException(); }
            if (executable == "wpr" && arguments[0] == "-stop") { await File.WriteAllTextAsync(arguments[1], "ETL fixture", cancellationToken); }
            return new(0, "", false, executable == "target.exe" ? 42 : null);
        }
    }
}
