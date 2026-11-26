using System.Text;
using CMakeWorkflow.Contracts.Workflows;
using System.IO.Enumeration;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Core;

namespace CMakeWorkflow.CMake.Quality;

internal static class FormatFiles
{
    private static readonly HashSet<string> CppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tpp",
        ".m", ".mm", ".ixx", ".cppm"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".cmakeworkflow", "_deps", "vcpkg_installed", "CMakeFiles", "out"
    };

    internal static FormatLanguage Language(string path) =>
        Path.GetFileName(path) == "CMakeLists.txt" || path.EndsWith(".cmake", StringComparison.Ordinal) ||
        path.EndsWith(".cmake.in", StringComparison.Ordinal) ? FormatLanguage.CMake :
        CppExtensions.Contains(Path.GetExtension(path)) ? FormatLanguage.Cpp : 0;

    internal static bool IsEligible(string root, string path, string? managedStore = null)
    {
        if (!File.Exists(path) || !WorkflowPlanner.PathComparer.Equals(path, BuildTreeOwnership.CanonicalPath(path)))
        {
            return false;
        }

        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        for (DirectoryInfo? parent = new FileInfo(path).Directory;
            parent is not null && !WorkflowPlanner.PathComparer.Equals(parent.FullName, root); parent = parent.Parent)
        {
            if (Excluded(parent, managedStore))
            {
                return false;
            }
        }
        return true;
    }

    internal static async Task<string[]> SelectAsync(string root, FormatRequest request, CancellationToken ct)
    {
        string? managedStore = ManagedStore();
        HashSet<string>? unstaged = null;
        IEnumerable<string> candidates;
        if (request.Selection == FormatSelection.Project)
        {
            candidates = request.Files ?? Discover(root, managedStore, ct);
        }
        else
        {
            candidates = await GitDiffAsync(root, request.Selection == FormatSelection.Staged ? "--cached" : "HEAD", request.Timeout, ct);
            if (request.Selection == FormatSelection.Staged)
            {
                unstaged = new(await GitDiffAsync(root, null, request.Timeout, ct), WorkflowPlanner.PathComparer);
            }
        }

        HashSet<string> selected = new(WorkflowPlanner.PathComparer);
        foreach (string input in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(input, root);
            FormatLanguage language = Language(path);
            if ((request.Languages & language) == 0)
            {
                if (request.Files is not null)
                {
                    throw new InvalidDataException($"Selected file does not match the formatter languages: {input}");
                }
                continue;
            }

            string relative = Relative(root, path);
            if ((request.Include is { } include && !Matches(include, relative)) ||
                (request.Exclude is { } exclude && Matches(exclude, relative)))
            {
                continue;
            }

            if (unstaged?.Contains(input) == true)
            {
                throw new InvalidDataException($"Staged formatter input differs from the working tree: {input}. Stage the edits or check --changed.");
            }
            if (!IsEligible(root, path, managedStore))
            {
                if (request.Files is not null)
                {
                    throw new InvalidDataException($"Selected formatter input is missing, excluded or traverses a link: {input}");
                }
                continue;
            }

            // The pinned formatter's discovery response is line-delimited and its file-list parser trims every entry.
            if (language == FormatLanguage.CMake && (relative.IndexOfAny(['\r', '\n']) >= 0 ||
                !string.Equals(relative, relative.TrimEnd(), StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"The cmakefmt discovery protocol cannot represent this path: {input}");
            }
            request.ReadGuard?.Invoke(path);
            _ = selected.Add(path);
        }

        return [.. selected.Order(StringComparer.Ordinal)];
    }

    internal static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    private static bool Matches(string expression, string relative) =>
        FileSystemName.MatchesSimpleExpression(expression, relative, OperatingSystem.IsWindows());

    private static IEnumerable<string> Discover(string root, string? managedStore, CancellationToken ct)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }
            foreach (DirectoryInfo child in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if (!Excluded(child, managedStore))
                {
                    pending.Push(child.FullName);
                }
            }
        }
    }

    internal static string? ManagedStore()
    {
        string? configured = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.IsPathFullyQualified(configured) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)) : null;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? null : Path.Combine(home, ".local", "share", "CMakeWorkflow");
    }

    private static bool Excluded(DirectoryInfo directory, string? managedStore) => ExcludedDirectories.Contains(directory.Name) ||
        WorkflowPlanner.PathComparer.Equals(directory.FullName, managedStore) ||
        (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
        File.Exists(Path.Combine(directory.FullName, "CMakeCache.txt"));

    private static async Task<string[]> GitDiffAsync(string root, string? revision, TimeSpan timeout, CancellationToken ct)
    {
        List<string> arguments = ["diff", "--name-only", "--relative", "--no-ext-diff", "-z"];
        if (revision is not null)
        {
            arguments.Add("--diff-filter=ACMRT");
            arguments.Add(revision);
        }
        arguments.Add("--");
        arguments.Add(".");
        CommandResult result = await CommandExecution.RunAsync("git", arguments, root, timeout, null, Encoding.UTF8, ct);
        return result.ExitCode != 0 || result.TimedOut
            ? throw new InvalidDataException("Could not select formatter inputs from Git. " + result.Stderr)
            : (result.Stdout ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
