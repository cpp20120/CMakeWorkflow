using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

public static partial class PerfStackAnalyzer
{
    // This field set is supported by the local perf. cpu is omitted because
    // per-task recordings may not contain PERF_SAMPLE_CPU. Default headers
    // with an optional [cpu] remain accepted for saved traces.
    [GeneratedRegex(@"^\s*([0-9a-fA-Fx]+)\s+(.+?)\s+\(([^()]*)\)\s*$")]
    private static partial Regex FramePattern();

    // -F ip,sym,dso may put the sampled leaf on the same line as the header.
    // Do not discard it: otherwise the normalizer can lose the hot function
    // even while correctly counting the sample and its callers.
    [GeneratedRegex(
        @"^(?<comm>.+?)\s+(?<pid>\d+)(?:/(?<tid>\d+))?\s+(?:\[\s*\d+\]\s+)?(?<time>\d+(?:\.\d+)?):\s*(?:\d+\s+)?(?<event>[A-Za-z][A-Za-z0-9_.:/-]*):(?:\s+(?<inline>.*))?$")]
    private static partial Regex SampleHeader();

    public static StackEvidence Parse(TextReader reader, CancellationToken ct)
    {
        var result = new StackEvidence();
        var frames = new List<string>(32);
        var inSample = false;
        var rejected = 0;
        var truncated = 0;
        var nonempty = false;
        var lines = 0;
        string? line;

        void AppendFrame(Match frame)
        {
            var address = frame.Groups[1].Value;
            var name = frame.Groups[2].Value.Trim();
            var dso = Path.GetFileName(frame.Groups[3].Value.Trim());
            var unresolved = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                             || name is "[unknown]" or "??";
            if (frames.Count < 256)
                frames.Add(unresolved
                    ? "[unknown] 0x" + address.TrimStart('0', 'x', 'X') + " @ " + dso
                    : name + " @ " + dso);
            else truncated++;
        }

        void Finish()
        {
            if (inSample) result.Add(frames);
            frames.Clear();
            inSample = false;
        }

        while ((line = reader.ReadLine()) is not null)
        {
            if ((++lines & 8191) == 0) ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                Finish();
                continue;
            }

            if (line.StartsWith('#')) continue;
            nonempty = true;
            var header = SampleHeader().Match(line);
            if (header.Success)
            {
                Finish();
                inSample = true;
                var inline = header.Groups["inline"].Value;
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    var sampledFrame = FramePattern().Match(inline);
                    if (sampledFrame.Success) AppendFrame(sampledFrame);
                    else rejected++;
                }

                continue;
            }

            var frame = FramePattern().Match(line);
            if (inSample && frame.Success) AppendFrame(frame);
            else rejected++;
        }

        Finish();
        result.RecordParseQuality(nonempty, rejected, truncated);
        return result;
    }

    public static async Task<string> AnalyzeAsync(string trace, string directory,
        string captureId, CancellationToken ct)
    {
        var file = Path.Combine(directory, "traces", "normalized", captureId + ".stacks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var start = new ProcessStartInfo("perf")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var arg in new[]
                 {
                     "script", "-i", trace, "-F",
                     "comm,pid,tid,time,period,event,ip,sym,dso"
                 }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start perf script.");
        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        var stderr = Task.Run(async () =>
        {
            var buffer = new char[4096];
            var saved = new StringBuilder();
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, ct)) > 0)
                if (saved.Length < 16384)
                    saved.Append(buffer, 0, Math.Min(read, 16384 - saved.Length));
            return saved.ToString();
        }, ct);
        var evidence = await Task.Run(() => Parse(process.StandardOutput, ct), ct);
        await process.WaitForExitAsync(ct);
        var errors = await stderr;
        if (process.ExitCode != 0) throw new IOException($"perf script failed ({process.ExitCode}): {errors}");
        if (evidence.AnalysisStatus == "unsupported_format")
            throw new InvalidDataException(
                "perf script produced nonempty output without compatible sample headers; raw trace is preserved.");
        await ProfileArtifactWriter.WriteTextAsync(file,
            evidence.ToJson("perf", captureId).ToJsonString() + "\n", ct);
        return file;
    }
}