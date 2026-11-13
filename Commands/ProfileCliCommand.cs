using CMakeWorkflow.Profiling;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Profiling.Replay;

namespace CMakeWorkflow.Commands;

internal static class ProfileCliCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] == "replay")
        {
            (string[] replayArgs, HistoryPolicy? replayPolicy) = CliHistory.Parse(args);
            return await CliHistory.RunAndIndexAsync(onRun => ProfileReplayCommand.ExecuteAsync(replayArgs, ct,
                onRun), replayPolicy);
        }

        if (args.Length == 0 || args[0] != "run")
        {
            return await ProfileCommand.ExecuteAsync(args, ct);
        }

        (string[] profileArgs, HistoryPolicy? historyPolicy) = CliHistory.Parse(args);
        return await CliHistory.RunAndIndexAsync(onRun => ProfileCommand.ExecuteAsync(profileArgs, ct,
            onRun), historyPolicy);
    }
}
