using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling.Analysis;

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

    public static async Task<StackEvidence> ParseAsync(TextReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StackEvidence result = new();
        List<string> frames = new(32);
        bool inSample = false;
        int rejected = 0;
        int truncated = 0;
        bool nonempty = false;
        string? line;

        void AppendFrame(Match frame)
        {
            string address = frame.Groups[1].Value;
            string name = frame.Groups[2].Value.Trim();
            string dso = Path.GetFileName(frame.Groups[3].Value.Trim());
            bool unresolved = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                             || name is "[unknown]" or "??";
            if (frames.Count < 256)
            {
                frames.Add(unresolved
                    ? "[unknown] 0x" + address.TrimStart('0', 'x', 'X') + " @ " + dso
                    : name + " @ " + dso);
            }
            else
            {
                truncated++;
            }
        }

        void Finish()
        {
            if (inSample)
            {
                result.Add(frames);
            }

            frames.Clear();
            inSample = false;
        }

        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                Finish();
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            nonempty = true;
            Match header = SampleHeader().Match(line);
            if (header.Success)
            {
                Finish();
                inSample = true;
                string inline = header.Groups["inline"].Value;
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    Match sampledFrame = FramePattern().Match(inline);
                    if (sampledFrame.Success)
                    {
                        AppendFrame(sampledFrame);
                    }
                    else
                    {
                        rejected++;
                    }
                }

                continue;
            }

            Match frame = FramePattern().Match(line);
            if (inSample && frame.Success)
            {
                AppendFrame(frame);
            }
            else
            {
                rejected++;
            }
        }

        Finish();
        result.RecordParseQuality(nonempty, rejected, truncated);
        return result;
    }

    public static async Task<string> AnalyzeAsync(string trace, string directory,
        string captureId, CancellationToken ct)
    {
        string file = Path.Combine(directory, "traces", "normalized", captureId + ".stacks.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        ProcessStartInfo start = new("perf")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        foreach (string? arg in new[]
                 {
                     "script", "-i", trace, "-F",
                     "comm,pid,tid,time,period,event,ip,sym,dso"
                 })
        {
            start.ArgumentList.Add(arg);
        }

        StackEvidence evidence = await PerfScript.RunAsync(start, ParseAsync, ct);

        if (evidence.AnalysisStatus == "unsupported_format")
        {
            throw new InvalidDataException(
                "perf script produced nonempty output without compatible sample headers; raw trace is preserved.");
        }

        await ProfileArtifactWriter.WriteTextAsync(file,
            evidence.ToJson("perf", captureId).ToJsonString() + "\n", ct);
        return file;
    }
}