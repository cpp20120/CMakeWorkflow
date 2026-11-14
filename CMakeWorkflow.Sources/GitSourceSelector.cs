using System.Diagnostics;
using System.Text;

namespace CMakeWorkflow.Sources;

/// <summary>Git index/file-system selection. File bytes always come from the working tree.</summary>
public sealed record GitSourceSelection(
    string RepositoryRoot,
    string? HeadCommit,
    IReadOnlyList<string> Files,
    int ExcludedUntrackedCount,
    int DeletedTrackedCount);

public sealed class GitSourceSelector(string gitExecutable = "git")
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _gitExecutable = !string.IsNullOrWhiteSpace(gitExecutable)
        ? gitExecutable : throw new ArgumentException("Git executable is required.", nameof(gitExecutable));

    /// <summary>
    /// Select tracked files present in the working tree, plus untracked non-ignored files only
    /// when opted in. Requires sourceRoot to be the Git toplevel. Never stages or writes to Git.
    /// Fails closed on index conflicts, symlinks, gitlinks and unsafe names.
    /// </summary>
    public GitSourceSelection Select(string sourceRoot, bool includeUntracked = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        string root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }
        // Git computes the prefix relative to the worktree root. This survives
        // filesystem aliases such as macOS /var -> /private/var, unlike comparing
        // two absolute path strings.
        string prefix = StrictUtf8.GetString(Run(root, true,
            "rev-parse", "--show-prefix").Output).TrimEnd('\r', '\n');
        if (prefix.Length != 0)
        {
            throw new InvalidDataException("The source root must be the Git worktree root, not a subdirectory.");
        }

        GitOutput head = Run(root, false, "rev-parse", "--verify", "HEAD");
        string? commit = head.ExitCode == 0
            ? StrictUtf8.GetString(head.Output).TrimEnd('\r', '\n') : null;

        SortedSet<string> selected = new(StringComparer.Ordinal);
        HashSet<string> portableNames = new(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        foreach (string row in ReadZeroTerminated(Run(root, true,
            "ls-files", "--stage", "--full-name", "-z").Output))
        {
            int tab = row.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                throw new InvalidDataException("Malformed Git index entry.");
            }
            string[] fields = row[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[2] != "0")
            {
                throw new InvalidDataException("Unmerged Git index; source snapshot refused.");
            }
            string path = row[(tab + 1)..];
            SourcePathPolicy.Validate(path);
            if (fields[0] is not ("100644" or "100755"))
            {
                throw new InvalidDataException($"Unsupported Git mode {fields[0]} for '{path}' (link/submodule).");
            }
            // A deleted tracked file is absent from the captured source tree, whether
            // the deletion is staged or unstaged. Preserve neither its old blob nor index bytes.
            string physical = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(physical) && !Directory.Exists(physical))
            {
                deleted++;
                continue;
            }
            AddPath(path, selected, portableNames);
        }

        int excludedUntracked = 0;
        foreach (string path in ReadZeroTerminated(Run(root, true,
            "ls-files", "--others", "--exclude-standard", "--full-name", "-z").Output))
        {
            if (!includeUntracked)
            {
                excludedUntracked++;
                continue;
            }
            SourcePathPolicy.Validate(path);
            AddPath(path, selected, portableNames);
        }
        return new GitSourceSelection(root, commit, [.. selected], excludedUntracked, deleted);
    }

    private static void AddPath(string path, SortedSet<string> selected, HashSet<string> names)
    {
        if (!selected.Add(path) || !names.Add(path))
        {
            throw new InvalidDataException($"Duplicate or cross-platform case-colliding path: {path}");
        }
    }

    private static IEnumerable<string> ReadZeroTerminated(byte[] output)
    {
        if (output.Length != 0 && output[^1] != 0)
        {
            throw new InvalidDataException("Truncated Git -z output.");
        }
        int start = 0;
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] != 0)
            {
                continue;
            }
            yield return StrictUtf8.GetString(output, start, i - start);
            start = i + 1;
        }
    }

    private GitOutput Run(string root, bool mustSucceed, params string[] arguments)
    {
        ProcessStartInfo start = new(_gitExecutable)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // No shell interpolation: paths, options and unusual filenames stay distinct argv tokens.
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)
            ?? throw new IOException("Failed to start Git executable.");
        Task<string> errorReader = process.StandardError.ReadToEndAsync();
        using MemoryStream bytes = new();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        process.WaitForExit();
        string error = errorReader.GetAwaiter().GetResult();
        return mustSucceed && process.ExitCode != 0
            ? throw new IOException($"git {arguments[0]} failed ({process.ExitCode}): {error.Trim()}")
            : new GitOutput(process.ExitCode, bytes.ToArray());
    }

    private sealed record GitOutput(int ExitCode, byte[] Output);
}
