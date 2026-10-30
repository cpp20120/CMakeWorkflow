using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace CMakeWorkflow.Profiling;

public sealed record EtwAnalysisResult(string EventDump, string Summary, string Samples,
    int CpuSamples, int ContextSwitchesIn, int ReadyEvents);

public sealed class EtwTraceAnalyzer(IProfileProcess? process = null)
{
    private readonly IProfileProcess process = process ?? new ProfileProcess();
    private readonly bool nativeProcess = process is null;

    public async Task<EtwAnalysisResult> AnalyzeAsync(string trace, string outputDirectory,
        string captureId, string executable, int? targetProcessId,
        DateTimeOffset? targetStartedAt, DateTimeOffset? targetFinishedAt, CancellationToken ct)
    {
        if (nativeProcess && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ETL conversion requires Windows tracerpt.");
        var dump = Path.Combine(outputDirectory, captureId + ".events.xml");
        var conversion = await process.RunAsync("tracerpt",
            [trace, "-o", dump, "-of", "XML", "-lr", "-y"], outputDirectory,
            TimeSpan.FromMinutes(15), null, ct);
        ProfileEnvironment.RequireSuccess(conversion, "tracerpt");
        if (!File.Exists(dump)) throw new IOException("tracerpt succeeded without an XML event dump.");
        return ParseXml(dump, outputDirectory, captureId, executable,
            targetProcessId, targetStartedAt, targetFinishedAt, ct);
    }

    // This parser also accepts saved tracerpt XML for deterministic tests on
    // non-Windows hosts. Event records are streamed, not loaded as one DOM.
    public static EtwAnalysisResult ParseXml(string dump, string outputDirectory,
        string captureId, string executable, int? targetProcessId,
        DateTimeOffset? targetStartedAt, DateTimeOffset? targetFinishedAt, CancellationToken ct)
    {
        var normalizedDirectory = Path.Combine(outputDirectory, "traces", "normalized");
        Directory.CreateDirectory(normalizedDirectory);
        var summaryPath = Path.Combine(normalizedDirectory, captureId + ".etw.json");
        var samplesPath = Path.Combine(normalizedDirectory, captureId + ".etw-samples.jsonl");
        var timelinePath = Path.Combine(normalizedDirectory, captureId + ".timeline.jsonl");
        var stackPath = Path.Combine(normalizedDirectory, captureId + ".stacks.json");
        var stacks = new StackEvidence();
        var timelineCount = 0;
        var timelineDropped = 0;
        var threadProcess = new Dictionary<int, int>();
        var threads = new Dictionary<int, ThreadCounts>();
        var readyAt = new Dictionary<int, DateTimeOffset>();
        var runningAt = new Dictionary<int, DateTimeOffset>();
        var totalEvents = 0;
        var cpuSamples = 0;
        var switchesIn = 0;
        var switchesOut = 0;
        var readyEvents = 0;
        var rawStacks = 0;
        string? imageName = null;
        using (var sampleWriter = new StreamWriter(samplesPath, false, new UTF8Encoding(false)))
        using (var timelineWriter = new StreamWriter(timelinePath, false, new UTF8Encoding(false)))
        using (var reader = XmlReader.Create(dump, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        }))
        {
            void Timeline(DateTimeOffset? time, int tid, string state, int? processor)
            {
                if (time is null) return;
                if (timelineCount >= 10000) { timelineDropped++; return; }
                timelineCount++;
                timelineWriter.WriteLine(new JsonObject
                {
                    ["timestamp"] = time.Value.ToString("O"), ["threadId"] = tid,
                    ["state"] = state, ["cpu"] = processor
                }.ToJsonString());
            }
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Event")
                {
                    if (!reader.Read()) break;
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                var record = (XElement)XNode.ReadFrom(reader);
                totalEvents++;
                var system = Child(record, "System");
                var execution = Child(system, "Execution");
                var headerPid = ParseInt(Attr(execution, "ProcessID"));
                var headerTid = ParseInt(Attr(execution, "ThreadID"));
                var cpu = ParseInt(Attr(execution, "ProcessorID"));
                var timestampText = Attr(Child(system, "TimeCreated"), "SystemTime");
                DateTimeOffset? timestamp = DateTimeOffset.TryParse(timestampText,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : null;
                var data = Child(record, "EventData")?.Elements()
                    .Where(x => x.Name.LocalName == "Data")
                    .GroupBy(x => Attr(x, "Name") ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Value.Trim(),
                        StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rendering = Child(record, "RenderingInfo");
                var eventName = Child(rendering, "EventName")?.Value.Trim() ?? "";
                var opcode = Child(rendering, "Opcode")?.Value.Trim()
                    ?? Child(system, "Opcode")?.Value.Trim() ?? "";
                var normalizedName = new string(eventName.Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant).ToArray());
                var payloadPid = Value(data, "ProcessId", "ProcessID", "PID");
                var payloadTid = Value(data, "TThreadId", "ThreadId", "ThreadID", "TID");
                var targetPid = targetProcessId;

                if (normalizedName == "process" && (opcode.Equals("Start", StringComparison.OrdinalIgnoreCase)
                    || opcode == "1") && payloadPid == targetPid)
                    imageName = data.GetValueOrDefault("ImageFileName");

                if (normalizedName == "thread")
                {
                    if (payloadTid is { } tid && payloadPid is { } pid)
                    {
                        if (opcode.Equals("Start", StringComparison.OrdinalIgnoreCase)
                            || opcode.Equals("DCStart", StringComparison.OrdinalIgnoreCase)
                            || opcode is "1" or "3") threadProcess[tid] = pid;
                        else if (opcode.Equals("End", StringComparison.OrdinalIgnoreCase)
                            || opcode.Equals("DCEnd", StringComparison.OrdinalIgnoreCase)
                            || opcode is "2" or "4")
                        {
                            threadProcess.Remove(tid);
                            readyAt.Remove(tid);
                            runningAt.Remove(tid);
                        }
                    }
                    continue;
                }
                if (targetPid is null || !WithinWindow(timestamp, targetStartedAt, targetFinishedAt)) continue;
                var eventPid = payloadPid ?? headerPid;
                var belongsToTarget = eventPid == targetPid
                    || (headerTid is { } knownTid && threadProcess.GetValueOrDefault(knownTid) == targetPid)
                    || (payloadTid is { } payloadThread && threadProcess.GetValueOrDefault(payloadThread) == targetPid);
                if (normalizedName is "sampledprofile" or "sampleprofile")
                {
                    if (!belongsToTarget) continue;
                    cpuSamples++;
                    var thread = payloadTid ?? headerTid;
                    if (thread is { } sampleTid) Counts(threads, sampleTid).Samples++;
                    var stack = record.Descendants().FirstOrDefault(x =>
                        x.Name.LocalName is "StackTrace" or "Stack")?.Value.Trim()
                        ?? data.GetValueOrDefault("StackTrace")
                        ?? data.GetValueOrDefault("Stack");
                    if (!string.IsNullOrEmpty(stack))
                    {
                        rawStacks++;
                        // tracerpt may expose only raw addresses: these are
                        // explicitly marked unresolved, not fabricated symbols.
                        var frames = stack.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                            .SelectMany(x => x.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                ? x.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [x])
                            .Select(x => x.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                ? "[unknown]" : x).ToArray();
                        stacks.Add(frames);
                    }
                    else stacks.Add([]);
                    var sample = new JsonObject
                    {
                        ["timestamp"] = timestamp?.ToString("O"),
                        ["processId"] = targetPid,
                        ["threadId"] = thread,
                        ["cpu"] = cpu,
                        ["event"] = eventName,
                        ["instructionPointer"] = data.GetValueOrDefault("InstructionPointer"),
                        ["rawStack"] = stack
                    };
                    sampleWriter.WriteLine(sample.ToJsonString());
                }
                else if (normalizedName is "cswitch" or "contextswitch")
                {
                    var next = Value(data, "NewThreadId", "NewThreadID");
                    var previous = Value(data, "OldThreadId", "OldThreadID");
                    if (next is { } nextTid && threadProcess.GetValueOrDefault(nextTid) == targetPid)
                    {
                        switchesIn++;
                        var counts = Counts(threads, nextTid);
                        counts.SwitchesIn++;
                        if (timestamp is { } switchTime)
                        {
                            if (readyAt.Remove(nextTid, out var readyTime) && switchTime >= readyTime)
                                counts.AddReadyLatency((switchTime - readyTime).TotalMilliseconds);
                            runningAt[nextTid] = switchTime;
                            Timeline(timestamp, nextTid, "running", cpu);
                        }
                    }
                    if (previous is { } previousTid && threadProcess.GetValueOrDefault(previousTid) == targetPid)
                    {
                        switchesOut++;
                        var counts = Counts(threads, previousTid);
                        counts.SwitchesOut++;
                        Timeline(timestamp, previousTid, "offcpu", cpu);
                        if (timestamp is { } switchTime
                            && runningAt.Remove(previousTid, out var runningTime)
                            && switchTime >= runningTime)
                            counts.RunningMilliseconds += (switchTime - runningTime).TotalMilliseconds;
                    }
                }
                else if (normalizedName is "readythread" or "threadready")
                {
                    var ready = Value(data, "TThreadId", "ThreadId", "ThreadID", "TID");
                    if (ready is { } readyTid && threadProcess.GetValueOrDefault(readyTid) == targetPid)
                    {
                        readyEvents++;
                        Counts(threads, readyTid).ReadyEvents++;
                        Timeline(timestamp, readyTid, "ready", cpu);
                        if (timestamp is { } readyTime) readyAt.TryAdd(readyTid, readyTime);
                    }
                }
            }
        }
        var threadRows = new JsonArray();
        var readyLatencyCount = 0;
        var readyLatencyMilliseconds = 0.0;
        var runningMilliseconds = 0.0;
        foreach (var (tid, counts) in threads.OrderBy(x => x.Key))
        {
            readyLatencyCount += counts.ReadyLatencyCount;
            readyLatencyMilliseconds += counts.ReadyLatencyMilliseconds;
            runningMilliseconds += counts.RunningMilliseconds;
            threadRows.Add((JsonNode?)new JsonObject
            {
                ["threadId"] = tid,
                ["cpuSamples"] = counts.Samples,
                ["contextSwitchesIn"] = counts.SwitchesIn,
                ["contextSwitchesOut"] = counts.SwitchesOut,
                ["readyEvents"] = counts.ReadyEvents,
                ["readyLatencySamples"] = counts.ReadyLatencyCount,
                ["readyLatencySumMilliseconds"] = JsonValue.Create(
                    counts.ReadyLatencyMilliseconds, ProfileJson.Default.Double),
                ["readyLatencyMaxMilliseconds"] = JsonValue.Create(
                    counts.ReadyLatencyMaxMilliseconds, ProfileJson.Default.Double),
                ["scheduledRunningMilliseconds"] = JsonValue.Create(
                    counts.RunningMilliseconds, ProfileJson.Default.Double)
            });
        }
        var summary = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["backend"] = "etw",
            ["source"] = "tracerpt XML export",
            ["targetProcessId"] = targetProcessId,
            ["targetExecutable"] = executable,
            ["observedImageName"] = imageName,
            ["targetStartedAt"] = targetStartedAt?.ToString("O"),
            ["targetFinishedAt"] = targetFinishedAt?.ToString("O"),
            ["attribution"] = targetProcessId is null ? "unresolved" : "target PID and time window",
            ["totalTraceEvents"] = totalEvents,
            ["targetCpuSamples"] = cpuSamples,
            ["targetContextSwitchesIn"] = switchesIn,
            ["targetContextSwitchesOut"] = switchesOut,
            ["targetReadyEvents"] = readyEvents,
            ["readyLatencySamples"] = readyLatencyCount,
            ["readyLatencySumMilliseconds"] = JsonValue.Create(
                readyLatencyMilliseconds, ProfileJson.Default.Double),
            ["scheduledRunningMilliseconds"] = JsonValue.Create(
                runningMilliseconds, ProfileJson.Default.Double),
            ["samplesWithRawStacks"] = rawStacks,
            ["samplesFile"] = Path.GetRelativePath(outputDirectory, samplesPath),
            ["threads"] = threadRows,
            ["measurementSemantics"] = "ETW event counts and observed scheduling intervals; CPU samples are statistical observations, not PMU cycles or elapsed time."
        };
        summary["timelineEventsRecorded"] = timelineCount;
        summary["timelineEventsDropped"] = timelineDropped;
        summary["timelineSemantics"] = "Ready/running/offcpu transitions for known target threads; offcpu is not necessarily sleeping. Truncated timelines omit later events.";
        File.WriteAllText(stackPath, stacks.ToJson("etw", captureId).ToJsonString() + "\n");
        File.WriteAllText(summaryPath,
            summary.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, TypeInfoResolver = ProfileJson.Default }) + "\n");
        return new EtwAnalysisResult(dump, summaryPath, samplesPath,
            cpuSamples, switchesIn, readyEvents);
    }

    private sealed class ThreadCounts
    {
        public int Samples;
        public int SwitchesIn;
        public int SwitchesOut;
        public int ReadyEvents;
        public int ReadyLatencyCount;
        public double ReadyLatencyMilliseconds;
        public double ReadyLatencyMaxMilliseconds;
        public double RunningMilliseconds;

        public void AddReadyLatency(double milliseconds)
        {
            ReadyLatencyCount++;
            ReadyLatencyMilliseconds += milliseconds;
            ReadyLatencyMaxMilliseconds = Math.Max(ReadyLatencyMaxMilliseconds, milliseconds);
        }
    }

    private static ThreadCounts Counts(Dictionary<int, ThreadCounts> counts, int tid)
    {
        if (!counts.TryGetValue(tid, out var value)) counts[tid] = value = new ThreadCounts();
        return value;
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static string? Attr(XElement? parent, string name) =>
        parent?.Attributes().FirstOrDefault(x => x.Name.LocalName == name)?.Value;

    private static int? Value(IReadOnlyDictionary<string, string> data, params string[] names)
    {
        foreach (var name in names)
            if (data.TryGetValue(name, out var value) && ParseInt(value) is { } result)
                return result;
        return null;
    }

    private static int? ParseInt(string? value)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[2..], NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var hex)) return hex;
        return int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var decimalValue) ? decimalValue : null;
    }

    private static bool WithinWindow(DateTimeOffset? timestamp,
        DateTimeOffset? start, DateTimeOffset? end) =>
        timestamp is null || (start is null || timestamp >= start.Value.AddMilliseconds(-50))
            && (end is null || timestamp <= end.Value.AddMilliseconds(50));
}
