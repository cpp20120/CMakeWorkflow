using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

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

    public static int Parse(TextReader reader, TextWriter writer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        int lines = 0;
        int emitted = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if ((++lines & 8191) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

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

            void Event(string? pid, string state)
            {
                if (pid is null || emitted >= 10000)
                {
                    return;
                }

                if (!int.TryParse(pid, out int threadId))
                {
                    return;
                }

                writer.WriteLine(new JsonObject
                {
                    ["timestamp"] = time,
                    ["threadId"] = threadId,
                    ["cpu"] = cpu,
                    ["state"] = state,
                    ["clock"] = "perf-relative",
                    ["attribution"] = "recorded tracepoint only"
                }.ToJsonString());
                emitted++;
            }

            if (match.Groups["kind"].Value == "switch")
            {
                Event(Field(data, "prev_pid"), "offcpu");
                Event(Field(data, "next_pid"), "running");
            }
            else
            {
                Event(Field(data, "pid"), "ready");
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

        using Process process = Process.Start(start) ?? throw new IOException("Could not start perf script.");
        using CancellationTokenRegistration registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        Task<string> stderr = Task.Run(async () =>
        {
            char[] buffer = new char[4096];
            StringBuilder saved = new();
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, ct)) > 0)
            {
                if (saved.Length < 16384)
                {
                    _ = saved.Append(buffer, 0, Math.Min(read, 16384 - saved.Length));
                }
            }

            return saved.ToString();
        }, ct);
        try
        {
            _ = await Task.Run(() =>
            {
                using StreamWriter writer = new(temp, false, new UTF8Encoding(false));
                return Parse(process.StandardOutput, writer, ct);
            }, ct);
            await process.WaitForExitAsync(ct);
            string errors = await stderr;
            if (process.ExitCode != 0)
            {
                throw new IOException(
                    $"perf script scheduler decode failed: {errors[..Math.Min(errors.Length, 4096)]}");
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