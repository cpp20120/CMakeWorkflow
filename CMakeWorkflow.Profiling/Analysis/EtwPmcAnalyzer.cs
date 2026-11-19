using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Capture;

namespace CMakeWorkflow.Profiling.Analysis;

internal static class EtwPmcAnalyzer
{
    private const string PerfInfo = "ce1dbfb4-137e-4da6-87b0-3f59aa102cbc";
    private const string Thread = "3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c";

    internal static async Task<string> AnalyzeAsync(string trace, string directory, string capture,
        string configuration, int pid, DateTimeOffset start, DateTimeOffset finish, CancellationToken ct)
    {
        string dump = Path.Combine(directory, capture + ".events.xml");
        CommandResult converted = await new ProfileProcess().RunAsync("tracerpt",
            [trace, "-o", dump, "-of", "XML", "-lr", "-y"], directory, TimeSpan.FromMinutes(15), null, ct);
        ProfileEnvironment.RequireSuccess(converted, "tracerpt");
        return await ParseXmlAsync(dump, directory, capture, configuration, pid, start, finish, ct);
    }

    internal static async Task<string> ParseXmlAsync(string dump, string directory, string capture,
        string configuration, int pid, DateTimeOffset start, DateTimeOffset finish, CancellationToken ct)
    {
        if (pid <= 0 || finish < start)
        {
            throw new InvalidDataException("PMC attribution requires a positive target PID and a valid launch time window.");
        }
        JsonObject config = JsonNode.Parse(await File.ReadAllTextAsync(configuration, ct))!.AsObject();
        if (config["schemaVersion"]?.GetValue<int>() != 1 || config["method"]?.GetValue<string>() != "etw-pmc-overflow")
        {
            throw new InvalidDataException("Unsupported PMC capture configuration.");
        }
        Dictionary<int, Counter> counters = [];
        foreach (JsonNode? node in config["sources"]!.AsArray())
        {
            JsonObject source = node!.AsObject();
            int interval = source["interval"]!.GetValue<int>();
            if (interval < 4096)
            {
                throw new InvalidDataException("Invalid PMC sampling interval.");
            }
            counters.Add(source["sourceId"]!.GetValue<int>(), new Counter(source));
        }
        Dictionary<int, int> threads = [];
        long ignored = 0;
        bool ordered = true;
        DateTimeOffset? previous = null;
        await using FileStream input = new(dump, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });
        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Event")
            {
                if (!await reader.ReadAsync()) { break; }
                continue;
            }
            XElement record = (XElement)await XNode.ReadFromAsync(reader, ct);
            XElement? system = Child(record, "System");
            // Real tracerpt exports use MSNT_SystemTrace for System.Provider.Guid,
            // while ExtendedTracingInfo.EventGuid identifies classic kernel events.
            // Keep the Provider fallback for traces/fixtures without ExtendedTracingInfo.
            string? provider = Child(Child(record, "ExtendedTracingInfo"), "EventGuid")?.Value.Trim().Trim('{', '}')
                ?? Child(system, "Provider")?.Attribute("Guid")?.Value.Trim('{', '}');
            string? opcode = Child(system, "Opcode")?.Value;
            string? stamp = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time))
            {
                if (string.Equals(provider, PerfInfo, StringComparison.OrdinalIgnoreCase)) { ordered = false; }
                continue;
            }
            if (previous > time) { ordered = false; }
            previous = time;
            Dictionary<string, string> data = Child(record, "EventData")?.Elements()
                .Where(e => e.Name.LocalName == "Data" && e.Attribute("Name") is not null)
                .ToDictionary(e => e.Attribute("Name")!.Value, e => e.Value.Trim(), StringComparer.OrdinalIgnoreCase) ?? [];
            int? Value(string name) => Number(data.GetValueOrDefault(name));
            if (string.Equals(provider, Thread, StringComparison.OrdinalIgnoreCase))
            {
                int? tid = Value("TThreadId") ?? Value("ThreadId");
                int? processId = Value("ProcessId");
                if (tid is { } threadId && processId is { } processIdValue)
                {
                    if (opcode is "1" or "3") { threads[threadId] = processIdValue; }
                    if (opcode is "2" or "4") { _ = threads.Remove(threadId); }
                }
                if (threads.Count > 65536) { throw new InvalidDataException("ETW thread attribution limit exceeded."); }
                continue;
            }
            if (!string.Equals(provider, PerfInfo, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (opcode is "72" or "73" && Value("SampleSource") is { } sourceId
                && counters.TryGetValue(sourceId, out Counter? activated) && time <= finish)
            {
                int? interval = Value("NewInterval");
                activated.Activated = interval == activated.Interval;
                if (!activated.Activated && time >= start) { activated.InvalidInterval = true; }
            }
            if (opcode != "47") { continue; }
            int? source = Value("ProfileSource");
            if (source is not { } id || !counters.TryGetValue(id, out Counter? counter)) { ignored++; continue; }
            int? sampleThread = Value("ThreadId");
            if (time < start || time > finish || sampleThread is not { } stid
                || threads.GetValueOrDefault(stid) != pid) { ignored++; continue; }
            counter.Samples++;
            if (!counter.Activated) { counter.InvalidInterval = true; }
        }

        JsonArray results = [];
        foreach ((int id, Counter counter) in counters)
        {
            bool supported = ordered && counter.Activated && !counter.InvalidInterval;
            results.Add((JsonNode)new JsonObject
            {
                ["sourceId"] = id,
                ["name"] = counter.Source["name"]!.GetValue<string>(),
                ["role"] = counter.Source["role"]!.GetValue<string>(),
                ["interval"] = counter.Interval,
                ["samples"] = counter.Samples,
                ["estimatedEvents"] = supported && counter.Samples > 0 ? (double)counter.Samples * counter.Interval : null,
                ["status"] = !supported ? "interval-or-order-unverified" : counter.Samples == 0 ? "no-target-samples" : "complete"
            });
        }
        string normalized = Path.Combine(directory, "traces", "normalized");
        _ = Directory.CreateDirectory(normalized);
        string path = Path.Combine(normalized, capture + ".etw-pmc.json");
        await File.WriteAllTextAsync(path, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["backend"] = "etw",
            ["method"] = "etw-pmc-overflow",
            ["targetProcessId"] = pid,
            ["targetStartedAt"] = start.ToString("O"),
            ["targetFinishedAt"] = finish.ToString("O"),
            ["attribution"] = "target PID, thread lifetime and launch time window; direct process only",
            ["configurationSha256"] = await ProfileArtifactHash.Sha256Async(configuration, ct),
            ["ignoredSamples"] = ignored,
            ["orderedEvents"] = ordered,
            ["counters"] = results
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        return path;
    }

    private static XElement? Child(XElement? element, string name) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static int? Number(string? text) => text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true
        ? int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null
        : int.TryParse(text, CultureInfo.InvariantCulture, out int number) ? number : null;

    private sealed class Counter(JsonObject source)
    {
        internal JsonObject Source { get; } = source;
        internal int Interval { get; } = source["interval"]!.GetValue<int>();
        internal long Samples { get; set; }
        internal bool Activated { get; set; }
        internal bool InvalidInterval { get; set; }
    }
}
