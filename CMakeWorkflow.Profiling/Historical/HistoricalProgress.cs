
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
using CMakeWorkflow.CMake.Execution;

namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

// A view of the durable command journal, not a second owner of operation state.
// The percentage counts finished command steps, never elapsed wall-clock time.
internal sealed class HistoricalProgress : IDisposable
{
    private readonly Lock gate = new();
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private readonly int total;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly bool interactive;
    private readonly TextWriter output;
    private string stage = "Preparing";
    private string active = "";
    private DateTimeOffset lastLog = DateTimeOffset.MinValue;
    private bool activeLine;

    public HistoricalProgress(int timingTrials, int statTrials, bool resume,
        TextWriter? output = null, bool? interactive = null)
    {
        this.output = output ?? Console.Error;
        this.interactive = interactive ?? !Console.IsErrorRedirected;
        total = TotalOperations(timingTrials, statTrials);
        this.output.WriteLine($"[CMakeWorkflow] Historical profile: {total} command operations" +
                              (resume ? "; verifying saved operations on resume" : "") +
                              ". Progress counts operations, not estimated runtime.");
    }

    internal static int TotalOperations(int timingTrials, int statTrials) =>
        checked(13 + (24 * (timingTrials + statTrials + 8)));

    public void Observe(WorkflowDispatchEvent item)
    {
        lock (gate)
        {
            string currentStage = StageOf(item.Label);
            if (item.Type == "started")
            {
                active = item.Label;
            }

            if (item.Type is "succeeded" or "verified")
            {
                _ = completed.Add(item.Label);
                if (active == item.Label)
                {
                    active = "";
                }
            }

            if (item.Type == "stopped" && active == item.Label)
            {
                active = "";
            }

            bool stageChanged = stage != currentStage;
            stage = currentStage;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // For pipes and CI, avoid hundreds of status lines. The periodic
            // heartbeat still makes long silent commands observable.
            if (!interactive && item.Type == "verified" && completed.Count % 25 != 0 && !stageChanged)
            {
                return;
            }

            if (!interactive && item.Type == "started" && !stageChanged && lastLog != DateTimeOffset.MinValue)
            {
                return;
            }

            if (!interactive && item.Type == "succeeded" && completed.Count % 25 != 0 && completed.Count != total)
            {
                return;
            }
            // The process owner emits heartbeats every 10s. Do not suppress the
            // first one in redirected output: otherwise a 15s silent process
            // looks completely stalled to CI, IDE logs and the operator.
            if (!interactive && item.Type == "heartbeat" && now - lastLog < TimeSpan.FromSeconds(8))
            {
                return;
            }

            string elapsed = FormatElapsed(now - started);
            string current = item.Type == "heartbeat" ? $" running {item.Label} ({FormatElapsed(item.Elapsed)})"
                : item.Type == "started" ? $" running {item.Label}"
                : item.Type == "stopped" ? $" stopped {item.Label}"
                : "";
            string line = $"[CMakeWorkflow] [{completed.Count}/{total} ops; {100.0 * completed.Count / total:0}%] " +
                       $"{stage}; elapsed {elapsed}{current}";
            if (interactive)
            {
                // Keep updates on one terminal row even with long scenario IDs.
                try
                {
                    int width = Console.WindowWidth - 1;
                    if (width > 10 && line.Length > width)
                    {
                        line = line[..(width - 1)] + "…";
                    }
                }
                catch (IOException)
                {
                    /* Terminal dimensions are advisory. */
                }

                output.Write("\r\u001b[2K" + line);
                activeLine = true;
                if (item.Type == "stopped" || completed.Count == total)
                {
                    FinishLine();
                }
            }
            else
            {
                output.WriteLine(line);
            }

            lastLog = now;
        }
    }

    public void Phase(string description)
    {
        lock (gate)
        {
            if (activeLine)
            {
                FinishLine();
            }

            output.WriteLine($"[CMakeWorkflow] {description}; {completed.Count}/{total} command operations verified.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            FinishLine();
        }
    }

    private void FinishLine()
    {
        if (activeLine)
        {
            output.WriteLine();
            activeLine = false;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    private static string StageOf(string label)
    {
        return label.StartsWith("time-", StringComparison.Ordinal)
            ? "Timing"
            : label.StartsWith("stat-", StringComparison.Ordinal)
            ? "Hardware counters"
            : label.StartsWith("periodic-", StringComparison.Ordinal)
            ? "DWARF capture and analysis"
            : label.StartsWith("fp-", StringComparison.Ordinal)
            ? "Frame-pointer diagnostics"
            : label.EndsWith("-configure", StringComparison.Ordinal) ||
            label.EndsWith("-build", StringComparison.Ordinal)
            ? "CMake configuration and build"
            : "Host preparation";
    }
}