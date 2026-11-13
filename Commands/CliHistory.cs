using CMakeWorkflow.History.Storage;
using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.Commands;

/// <summary>Strips CLI history options and indexes retained results after execution or cancellation.</summary>
/// <remarks>Expected indexing failures are diagnostic only and do not replace the workflow result.</remarks>
internal static class CliHistory
{
    internal static (string[] Args, HistoryPolicy? Policy) Parse(string[] args)
    {
        List<string> clean = new(args.Length);
        string? database = null;
        bool disabled = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                clean.AddRange(args[i..]);
                break;
            }

            if (args[i] == "--no-history")
            {
                disabled = true;
                continue;
            }

            if (args[i] == "--history-db")
            {
                if (database is not null || i + 1 >= args.Length
                                         || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("--history-db requires one database path.");
                }

                database = args[++i];
                continue;
            }

            clean.Add(args[i]);
        }

        return disabled && database is not null
            ? throw new ArgumentException("--no-history and --history-db cannot be combined.")
            : (clean.ToArray(), disabled ? new(HistoryPolicyMode.Disabled)
                : database is null ? null : new(HistoryPolicyMode.ExplicitDatabase, database));
    }

    internal static async Task<int> RunAndIndexAsync(Func<Action<string>, Task<int>> run,
        HistoryPolicy? policy)
    {
        string? directory = null;
        try
        {
            return await run(path => directory = path);
        }
        finally
        {
            if (policy is not { Mode: HistoryPolicyMode.Disabled } && directory is not null)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, "run.json")) ||
                        Directory.EnumerateFiles(directory, "*.results.json").Any())
                    {
                        string path = policy?.DatabasePath ?? HistoryStore.DefaultDatabasePath;
                        using HistoryStore store = new(path);
                        string summary = store.Import(directory, recursive: false);
                        await Console.Error.WriteLineAsync($"[CMakeWorkflow] History: {summary} Database: {Path.GetFullPath(path)}");
                    }
                }
                catch (Exception error) when (CliErrors.IsExpected(error))
                {
                    await Console.Error.WriteLineAsync($"[CMakeWorkflow] History indexing failed: {error.Message}");
                }
            }
        }
    }
}
