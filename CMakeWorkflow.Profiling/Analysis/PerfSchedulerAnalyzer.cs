using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling.Analysis;

// A relative timeline from per-task perf tracepoints. It is not a complete
// system-wide scheduler trace: wakeups originating in other tasks may be absent.
public static partial class PerfSchedulerAnalyzer
{
    [GeneratedRegex(@"\[\s*(?<cpu>\d+)\]\s+(?<time>\d+\.\d+):\s+sched:sched_(?<kind>switch|wakeup):\s+(?<data>.+)")]
    private static partial Regex Header();

    [GeneratedRegex(@"(?:^|\s)(?<key>prev_pid|next_pid|pid)=(?<pid>\d+)(?:\s|$)")]
    private static partial Regex Pid();

    private static string? Field(string data, string key) => Pid().Matches(data)
        .Select(match => match.Groups["key"].Value == key ? match.Groups["pid"].Value : null)
        .FirstOrDefault(value => value is not null);

    public static async Task<int> ParseAsync(TextReader reader, TextWriter writer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        int emitted = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            Match match = Header().Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!double.TryParse(match.Groups["time"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds < 0)
            {
                continue;
            }

            string time = DateTimeOffset.UnixEpoch.AddSeconds(seconds).ToString("O");
            int cpu = int.Parse(match.Groups["cpu"].Value, CultureInfo.InvariantCulture);
            string data = match.Groups["data"].Value;

            async Task EventAsync(string? pid, string state)
            {
                if (pid is null || emitted >= 10000)
                {
                    return;
                }

                if (!int.TryParse(pid, out int threadId))
                {
                    return;
                }

                await writer.WriteLineAsync(new JsonObject
                {
                    ["timestamp"] = time,
                    ["threadId"] = threadId,
                    ["cpu"] = cpu,
                    ["state"] = state,
                    ["clock"] = "perf-relative",
                    ["attribution"] = "recorded tracepoint only"
                }.ToJsonString().AsMemory(), ct);
                emitted++;
            }

            if (match.Groups["kind"].Value == "switch")
            {
                await EventAsync(Field(data, "prev_pid"), "offcpu");
                await EventAsync(Field(data, "next_pid"), "running");
            }
            else
            {
                await EventAsync(Field(data, "pid"), "ready");
            }
        }

        return emitted;
    }

    public static async Task<string> AnalyzeAsync(string raw, string directory, string captureId, CancellationToken ct)
    {
        string file = Path.Combine(directory, "traces", "normalized", captureId + ".timeline.jsonl");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        ProcessStartInfo start = new("perf")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        foreach (string? argument in new[] { "script", "--ns", "-i", raw })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            await using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            await using (StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                _ = await PerfScript.RunAsync(start, (reader, token) => ParseAsync(reader, writer, token), ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, file, overwrite: true);
            return file;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}