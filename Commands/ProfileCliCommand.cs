using CMakeWorkflow.Profiling;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Profiling.Analysis;
using CMakeWorkflow.Profiling.Replay;

namespace CMakeWorkflow.Commands;

internal static class ProfileCliCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] == "replay")
        {
            (string[] replayArgs, HistoryPolicy? replayPolicy) = CliHistory.Parse(args);
            return await CliHistory.RunAndIndexAsync(onRun => replayArgs is [.. string[] replay, "--html"]
                ? ReplayHtmlAsync(replay, onRun, ct)
                : ProfileReplayCommand.ExecuteAsync(replayArgs, ct, onRun), replayPolicy);
        }

        if (args.Length == 0 || args[0] != "run")
        {
            return await ProfileCommand.ExecuteAsync(args, ct);
        }

        (string[] profileArgs, HistoryPolicy? historyPolicy) = CliHistory.Parse(args);
        return await CliHistory.RunAndIndexAsync(onRun => ProfileCommand.ExecuteAsync(profileArgs, ct,
            onRun), historyPolicy);
    }

    private static async Task<int> ReplayHtmlAsync(string[] args, Action<string> onRun, CancellationToken ct)
    {
        string? directory = null;
        int result;
        try
        {
            result = await ProfileReplayCommand.ExecuteAsync(args, ct, path =>
            {
                directory = path;
                onRun(path);
            });
        }
        catch (Exception error) when (directory is not null && CliErrors.IsExpected(error))
        {
            try
            {
                if (Directory.EnumerateFiles(directory, "*.manifest.json").Any())
                {
                    if (!File.Exists(Path.Combine(directory, "analysis.json")))
                    {
                        _ = await ProfileAnalyzer.AnalyzeAsync(directory, ct, emitResultPath: false);
                    }

                    _ = await WorkflowHtmlReport.ExecuteAsync(directory, ct);
                }
            }
            catch (Exception reportError) when (CliErrors.IsExpected(reportError))
            {
                await Console.Error.WriteLineAsync($"[CMakeWorkflow] Could not export partial replay: {reportError.Message}");
            }

            throw;
        }

        return result == 0 && directory is not null ? await WorkflowHtmlReport.ExecuteAsync(directory, ct) : result;
    }
}
