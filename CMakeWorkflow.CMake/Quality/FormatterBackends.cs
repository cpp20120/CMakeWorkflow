using System.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;

namespace CMakeWorkflow.CMake.Quality;

public static partial class SourceFormatting
{
    private static async Task<bool> RunCMakeAsync(string root, FormatRequest request, FormatToolIdentity tool,
        string[] files, ConcurrentDictionary<string, FormatFile> results, ConcurrentQueue<FormatInvocation> invocations, CancellationToken ct)
    {
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-format-");
        try
        {
            Dictionary<string, string?> environment = new(StringComparer.Ordinal)
            {
                ["HOME"] = scratch.FullName,
                ["USERPROFILE"] = scratch.FullName,
                ["XDG_CONFIG_HOME"] = scratch.FullName,
                ["GIT_CONFIG_GLOBAL"] = Path.Combine(scratch.FullName, "gitconfig"),
                ["GIT_CONFIG_NOSYSTEM"] = "1"
            };
            string[] version = tool is { Origin: "managed", Version: { } pin } ? ["--required-version", pin] : [];
            CommandResult discovered = await CommandExecution.RunAsync(tool.Executable,
                [.. version, "--list-input-files", "--color", "never", "."], root, request.Timeout, environment, Encoding.UTF8, ct);
            invocations.Enqueue(new("cmakefmt", "discover", discovered.ExitCode, discovered.Stderr ?? ""));
            if (discovered.ExitCode != 0 || discovered.TimedOut)
            {
                return false;
            }

            HashSet<string> visible = new((discovered.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.GetFullPath(path.TrimEnd('\r'), root)), WorkflowPlanner.PathComparer);
            string[] selected = [.. files.Where(visible.Contains)];
            foreach (string file in files.Where(file => !visible.Contains(file)))
            {
                results[file] = new(FormatFiles.Relative(root, file), FormatStatus.Skipped, "Excluded by cmakefmt discovery rules.");
            }
            if (selected.Length == 0)
            {
                return true;
            }

            string list = Path.Combine(scratch.FullName, "files");
            await File.WriteAllTextAsync(list, string.Join('\0', selected.Select(file => "./" + FormatFiles.Relative(root, file))) + '\0', ct);
            string mode = request.Mode switch
            {
                FormatMode.Apply => "--in-place",
                FormatMode.Diff => "--diff",
                FormatMode.Check => "--check",
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            Dictionary<string, string>? before = request.Mode == FormatMode.Apply ? await SnapshotAsync(selected, ct) : null;
            bool valid = false;
            try
            {
                RequireEligible(root, selected);
                CommandResult execution = await CommandExecution.RunAsync(tool.Executable,
                    [.. version, mode, "--files-from", list, "--verify", "--keep-going", "--report-format", "json",
                        "--color", "never", "--parallel", request.Jobs.ToString(CultureInfo.InvariantCulture)],
                    root, request.Timeout, environment, Encoding.UTF8, ct);
                valid = ReadCMakeReport(root, selected, request.Mode, execution.Stdout ?? "", results, out string? diagnostic) &&
                    !execution.TimedOut && (execution.ExitCode == 0 || (request.Mode == FormatMode.Check && execution.ExitCode == 1 &&
                        selected.Any(file => results[file].Status == FormatStatus.FormattingRequired)));
                string error = string.Join(Environment.NewLine, new[] { execution.TimedOut ? "cmakefmt timed out." : null,
                    execution.Stderr, diagnostic }.Where(message => !string.IsNullOrWhiteSpace(message)));
                invocations.Enqueue(new("cmakefmt", "format", execution.ExitCode, error, execution.Stdout));
                return valid;
            }
            finally
            {
                if (before is not null)
                {
                    await RecordAppliedAsync(root, before, results, valid, ct.IsCancellationRequested);
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static bool ReadCMakeReport(string root, string[] selected, FormatMode mode, string json,
        ConcurrentDictionary<string, FormatFile> results, out string? diagnostic)
    {
        diagnostic = null;
        Dictionary<string, FormatFile> reported = new(WorkflowPlanner.PathComparer);
        HashSet<string> scope = new(selected, WorkflowPlanner.PathComparer);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement item in document.RootElement.GetProperty("files").EnumerateArray())
            {
                string path = Path.GetFullPath(item.GetProperty("path").GetString()!, root);
                FormatStatus status = item.GetProperty("skipped").GetBoolean() ? FormatStatus.Skipped :
                    item.GetProperty("would_change").GetBoolean() ? FormatStatus.FormattingRequired : FormatStatus.Clean;
                Add(path, status, item.GetProperty("skip_reason").GetString(), item.GetProperty("diff").GetString());
            }
            HashSet<string> unboundErrors = new(StringComparer.Ordinal);
            foreach (JsonElement error in document.RootElement.GetProperty("errors").EnumerateArray())
            {
                string path = Path.GetFullPath(error.GetProperty("display_name").GetString()!, root);
                string detail = error.GetProperty("error").GetString() ?? "cmakefmt failed without a diagnostic.";
                if (scope.Contains(path))
                {
                    Add(path, FormatStatus.Failed, detail, null);
                }
                else
                {
                    // cmakefmt attributes configuration failures to the config path, not to a source file.
                    _ = unboundErrors.Add(detail);
                }
            }

            foreach ((string file, FormatFile value) in reported)
            {
                results[file] = value;
            }
            string expectedMode = mode == FormatMode.Apply ? "in-place" : mode.ToString();
            if (unboundErrors.Count > 0)
            {
                diagnostic = string.Join(Environment.NewLine, unboundErrors);
                return false;
            }
            if (reported.Count != selected.Length ||
                !string.Equals(document.RootElement.GetProperty("mode").GetString(), expectedMode, StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = "cmakefmt did not report the expected mode and selected files.";
                return false;
            }
            return reported.Values.All(file => file.Status != FormatStatus.Failed);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
                                      or KeyNotFoundException or InvalidDataException)
        {
            diagnostic = "Invalid cmakefmt report. " + error.Message;
            return false;
        }

        void Add(string path, FormatStatus status, string? detail, string? diff)
        {
            if (!scope.Contains(path) || !reported.TryAdd(path, new(FormatFiles.Relative(root, path), status, detail, diff)))
            {
                throw new InvalidDataException("cmakefmt reported an unexpected or duplicate input.");
            }
        }
    }

    private static async Task<bool> RunClangAsync(string root, FormatRequest request, string executable, string[] files,
        ConcurrentDictionary<string, FormatFile> results, ConcurrentQueue<FormatInvocation> invocations, CancellationToken ct)
    {
        int failures = 0;
        await Parallel.ForEachAsync(ClangBatches(files, request.BatchSize),
            new ParallelOptions { MaxDegreeOfParallelism = request.Jobs, CancellationToken = ct }, async (batch, token) =>
            {
                bool apply = request.Mode == FormatMode.Apply;
                Dictionary<string, string>? before = apply ? await SnapshotAsync(batch, token) : null;
                bool succeeded = false;
                try
                {
                    RequireEligible(root, batch);
                    CommandResult execution = await CommandExecution.RunAsync(executable,
                        [apply ? "-i" : "--output-replacements-xml", "--", .. batch], root, request.Timeout, null, token);
                    succeeded = execution.ExitCode == 0 && !execution.TimedOut;
                    invocations.Enqueue(new("clang-format", "format", execution.ExitCode,
                        execution.TimedOut ? "clang-format timed out. " + execution.Stderr : execution.Stderr ?? ""));
                    if (!succeeded)
                    {
                        _ = Interlocked.Increment(ref failures);
                    }
                    if (succeeded && !apply)
                    {
                        ReadClangReport(root, batch, execution.Stdout ?? "", results);
                    }
                }
                finally
                {
                    if (before is not null)
                    {
                        await RecordAppliedAsync(root, before, results, succeeded, token.IsCancellationRequested);
                    }
                }
            });
        return failures == 0;
    }

    private static void ReadClangReport(string root, string[] files, string xml, ConcurrentDictionary<string, FormatFile> results)
    {
        const string declaration = "<?xml version='1.0'?>";
        string[] documents = xml.Split(declaration, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (documents.Length != files.Length)
        {
            return;
        }
        for (int index = 0; index < files.Length; index++)
        {
            string file = files[index];
            try
            {
                XElement report = XElement.Parse(documents[index]);
                FormatStatus status = report.Name != "replacements" || (string?)report.Attribute("incomplete_format") == "true"
                    ? FormatStatus.Failed : report.Elements("replacement").Any() ? FormatStatus.FormattingRequired : FormatStatus.Clean;
                results[file] = new(FormatFiles.Relative(root, file), status);
            }
            catch (XmlException error)
            {
                results[file] = new(FormatFiles.Relative(root, file), FormatStatus.Failed, error.Message);
            }
        }
    }

    private static IEnumerable<string[]> ClangBatches(string[] files, int batchSize)
    {
        List<string> batch = [];
        int estimate = 0;
        foreach (string file in files)
        {
            // Bound argv for long paths and Windows quoting as well as by file count.
            int cost = (file.Length * 4) + 8;
            if (batch.Count > 0 && (batch.Count >= batchSize || estimate + cost > 24000))
            {
                yield return [.. batch];
                batch.Clear();
                estimate = 0;
            }
            batch.Add(file);
            estimate += cost;
        }
        if (batch.Count > 0)
        {
            yield return [.. batch];
        }
    }

    private static void RequireEligible(string root, IEnumerable<string> files)
    {
        string? managedStore = FormatFiles.ManagedStore();
        foreach (string file in files)
        {
            if (!FormatFiles.IsEligible(root, file, managedStore))
            {
                throw new InvalidDataException($"Formatter input changed after selection: {file}");
            }
        }
    }

    private static async Task<Dictionary<string, string>> SnapshotAsync(string[] files, CancellationToken ct)
    {
        Dictionary<string, string> hashes = new(WorkflowPlanner.PathComparer);
        foreach (string file in files)
        {
            hashes.Add(file, await CommandExecution.Sha256Async(file, ct));
        }
        return hashes;
    }

    private static async Task RecordAppliedAsync(string root, Dictionary<string, string> before,
        ConcurrentDictionary<string, FormatFile> results, bool succeeded, bool cancelled)
    {
        foreach ((string file, string hash) in before)
        {
            string path = FormatFiles.Relative(root, file);
            try
            {
                if (!FormatFiles.IsEligible(root, file))
                {
                    results[file] = new(path, FormatStatus.Failed, "Source is missing or inaccessible after apply.");
                }
                else if (await CommandExecution.Sha256Async(file, CancellationToken.None) != hash)
                {
                    results[file] = new(path, FormatStatus.Formatted, results.GetValueOrDefault(file)?.Detail);
                }
                else if (!results.TryGetValue(file, out FormatFile? reported) || reported.Status == FormatStatus.FormattingRequired)
                {
                    results[file] = new(path, cancelled ? FormatStatus.Cancelled :
                        succeeded && reported is null ? FormatStatus.Clean : FormatStatus.Unprocessed);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                results[file] = new(path, FormatStatus.Failed, error.Message);
            }
        }
    }
}
