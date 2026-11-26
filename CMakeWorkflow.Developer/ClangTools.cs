using CMakeWorkflow.Contracts.Workflows;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Core;
using CMakeWorkflow.CMake.Quality;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Developer;

public enum ClangTool { Format, Tidy, Check, Analyzer, IncludeWhatYouUse, ApplyReplacements }

public sealed record ClangToolRequest(string ProjectDirectory, ClangTool Tool)
{
    public string? Target { get; init; }
    public string? BuildDirectory { get; init; }
    public string? Preset { get; init; }
    public string? Configuration { get; init; }
    public string? Executable { get; init; }
    public string? Checks { get; init; }
    public string? FixesDirectory { get; init; }
    public bool ApplyFormatting { get; init; }
    public int Jobs { get; init; } = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
    public int BatchSize { get; init; } = 128;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record FormatTarget(string Name, string? BuildDirectory = null, string? Preset = null, string? Configuration = null);

public sealed record ClangToolDiagnostic(string Input, int ExitCode, bool TimedOut, string StandardOutput, string StandardError);
public sealed record ClangToolResult(int Files, int Invocations, int FailedInvocations);

/// <summary>Runs local Clang tooling against CMake's existing source and compiler metadata.</summary>
public static class ClangTools
{
    private const string FixesLockName = ".cmwf-clang-fixes.lock";
    public static async Task<FormatResult> FormatAsync(FormatRequest request, FormatTarget target,
        Action<ClangToolDiagnostic>? report = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        if (request.Languages != FormatLanguage.Cpp || request.Files is not null || request.Selection != FormatSelection.Project ||
            (target.Preset is not null && target.BuildDirectory is not null))
        {
            throw new ArgumentException("Target formatting requires C++ project selection and at most one of preset or build directory.", nameof(target));
        }

        string root = BuildTreeOwnership.CanonicalPath(request.ProjectDirectory);
        string build = target.Preset is { } preset ? PresetBuildDirectory(root, preset)
            : target.BuildDirectory is { } directory ? Path.GetFullPath(directory, root)
            : FindBuildDirectory(root);
        using BuildTreeOwnership? lease = target.Preset is null ? null : await BuildTreeOwnership.AcquireAsync([build], ct);
        if (target.Preset is not null)
        {
            await ConfigureAsync(root, target.Preset, build, request.Timeout, report, ct);
        }
        CMakeSourceFiles source = await CMakeInspection.ReadTargetSourcesAsync(build, target.Name, target.Configuration, ct);
        RequireSourceRoot(root, source);
        return await SourceFormatting.RunAsync(request with { Files = [.. source.Files.Where(file => EligibleSource(root, file))] }, ct);
    }

    public static async Task<ClangToolResult> RunAsync(ClangToolRequest request,
        Action<ClangToolDiagnostic>? report = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Jobs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Jobs, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);
        if ((request.Checks is not null && request.Tool != ClangTool.Tidy) ||
            (request.FixesDirectory is not null && request.Tool is not (ClangTool.Tidy or ClangTool.Analyzer or ClangTool.ApplyReplacements)))
        {
            throw new ArgumentException("Check selection and fix export require clang-tidy.", nameof(request));
        }
        string root = BuildTreeOwnership.CanonicalPath(request.ProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!File.Exists(Path.Combine(root, "CMakeLists.txt")))
        {
            throw new InvalidDataException("Select a project directory containing CMakeLists.txt.");
        }

        if (request.Tool == ClangTool.Format)
        {
            FormatRequest format = new(root)
            {
                Languages = FormatLanguage.Cpp,
                Mode = request.ApplyFormatting ? FormatMode.Apply : FormatMode.Check,
                Executable = request.Executable,
                Jobs = request.Jobs,
                BatchSize = request.BatchSize,
                Timeout = request.Timeout
            };
            FormatResult formatted = request.Target is { } name
                ? await FormatAsync(format, new(name, request.BuildDirectory, request.Preset, request.Configuration), report, ct)
                : await SourceFormatting.RunAsync(format, ct);
            ct.ThrowIfCancellationRequested();
            foreach (FormatInvocation invocation in formatted.Invocations)
            {
                report?.Invoke(new(invocation.Tool, invocation.ExitCode ?? 2, false, "", invocation.Error));
            }
            int failed = formatted.Invocations.Count(invocation => invocation.ExitCode != 0);
            return new(formatted.Files.Count, formatted.Invocations.Count,
                formatted.ExitCode == 0 ? 0 : Math.Max(1, failed));
        }

        if (request.Tool == ClangTool.ApplyReplacements)
        {
            string replacements = Path.GetFullPath(request.FixesDirectory ??
                throw new ArgumentException("A replacements directory is required.", nameof(request)), root);
            using FileStream fixesLease = LockFixes(replacements);
            return await ExecuteAsync(request, root, 0, [new(replacements, [replacements])], report, ct);
        }

        if (request.Preset is not null && request.BuildDirectory is not null)
        {
            throw new ArgumentException("--preset and --build-dir are mutually exclusive.", nameof(request));
        }
        string build = request.Preset is { } preset ? PresetBuildDirectory(root, preset)
            : request.BuildDirectory is { } specified ? Path.GetFullPath(specified, root)
            : FindBuildDirectory(root);
        using BuildTreeOwnership? lease = request.Preset is null ? null : await BuildTreeOwnership.AcquireAsync([build], ct);
        if (request.Preset is not null)
        {
            await ConfigureAsync(root, request.Preset, build, request.Timeout, report, ct);
        }
        CMakeSourceFiles? target = request.Target is { } targetName
            ? await CMakeInspection.ReadTargetSourcesAsync(build, targetName, request.Configuration, ct) : null;
        if (target is not null)
        {
            RequireSourceRoot(root, target);
        }

        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-clang-");
        try
        {
            CompilationDatabase.TranslationUnit[] sources = await CompilationDatabase.SelectAsync(root, build, scratch.FullName, request,
                target, ct);
            if (sources.Length == 0)
            {
                throw new InvalidDataException("No translation units match the requested source tree, target and configuration.");
            }

            string? fixes = request.FixesDirectory is { } fixesPath ? Path.GetFullPath(fixesPath, root) : null;
            if (fixes is not null)
            {
                _ = Directory.CreateDirectory(fixes);
            }
            using FileStream? fixesLease = fixes is null ? null : LockFixes(fixes);
            if (fixes is not null && Directory.EnumerateFileSystemEntries(fixes)
                .Any(path => Path.GetFileName(path) != FixesLockName))
            {
                throw new InvalidDataException("Export fixes to an empty directory.");
            }
            IEnumerable<ToolInvocation> work = request.Tool == ClangTool.IncludeWhatYouUse
                ? [new("compilation database", ["-p", scratch.FullName, "-j", request.Jobs.ToString(System.Globalization.CultureInfo.InvariantCulture)])]
                : sources.Select((source, index) => new ToolInvocation(source.File,
                    AnalysisArguments(request, source.DatabaseDirectory, source.File, fixes, index)));
            return await ExecuteAsync(request, root, sources.Length, work, report, ct);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static FileStream LockFixes(string directory) =>
        new(Path.Combine(directory, FixesLockName), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);

    private static string FindBuildDirectory(string root)
    {
        string conventional = Path.Combine(root, "build");
        return File.Exists(Path.Combine(root, "CMakeCache.txt")) ? root
            : Directory.Exists(conventional) ? conventional
            : throw new InvalidDataException("No configured tree found. Provide --build-dir or --preset.");
    }

    private static string PresetBuildDirectory(string root, string preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset);
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preset)));
        string path = Path.Combine(root, ".cmakeworkflow", "clang", key);
        return WorkflowPlanner.PathComparer.Equals(path, BuildTreeOwnership.CanonicalPath(path))
            ? path : throw new InvalidDataException("The preset tooling directory traverses a filesystem link.");
    }

    private static async Task ConfigureAsync(string root, string preset, string build, TimeSpan timeout,
        Action<ClangToolDiagnostic>? report, CancellationToken ct)
    {
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", ct);
        CommandResult configured = await CommandExecution.RunAsync("cmake",
            [$"--preset={preset}", "-B", build, "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"], root, timeout, null, ct);
        report?.Invoke(new("CMake configure", configured.ExitCode, configured.TimedOut, configured.Stdout ?? "", configured.Stderr ?? ""));
        if (configured.ExitCode != 0 || configured.TimedOut)
        {
            throw new InvalidDataException("CMake could not configure the Clang tooling tree.");
        }
    }

    private static void RequireSourceRoot(string root, CMakeSourceFiles target)
    {
        if (!WorkflowPlanner.PathComparer.Equals(root, target.SourceDirectory))
        {
            throw new InvalidDataException("The CMake target belongs to a different source directory.");
        }
    }

    internal static bool EligibleSource(string root, string file) =>
        FormatFiles.Language(file) == FormatLanguage.Cpp && FormatFiles.IsEligible(root, file);

    private static string[] AnalysisArguments(ClangToolRequest request, string database, string source,
        string? fixes, int index)
    {
        List<string> arguments = ["-p", database];
        if (request.Tool != ClangTool.Check)
        {
            arguments.Add("--warnings-as-errors=*");
            if (request.Tool == ClangTool.Analyzer || request.Checks is not null)
            {
                arguments.Add("--checks=" + (request.Tool == ClangTool.Analyzer
                    ? "-*,clang-analyzer-*" : request.Checks));
            }
            if (fixes is not null)
            {
                arguments.Add("--export-fixes=" + Path.Combine(fixes, $"tu-{index:D6}.yaml"));
            }
        }
        arguments.Add(source);
        return [.. arguments];
    }

    private static async Task<ClangToolResult> ExecuteAsync(ClangToolRequest request, string root, int files,
        IEnumerable<ToolInvocation> work, Action<ClangToolDiagnostic>? report, CancellationToken ct)
    {
        string executable = request.Executable ?? request.Tool switch
        {
            ClangTool.Format => throw new InvalidOperationException("Formatting uses the shared formatter service."),
            ClangTool.Tidy or ClangTool.Analyzer => "clang-tidy",
            ClangTool.Check => "clang-check",
            ClangTool.IncludeWhatYouUse => new SystemToolingRuntime().Resolve("iwyu_tool", "") ?? "iwyu_tool.py",
            ClangTool.ApplyReplacements => "clang-apply-replacements",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        int invocations = 0;
        int failures = 0;
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = request.Jobs, CancellationToken = ct },
            async (invocation, token) =>
            {
                CommandResult result = await CommandExecution.RunAsync(executable, invocation.Arguments,
                    root, request.Timeout, null, token);
                _ = Interlocked.Increment(ref invocations);
                if (result.ExitCode != 0 || result.TimedOut)
                {
                    _ = Interlocked.Increment(ref failures);
                }
                report?.Invoke(new(invocation.Input, result.ExitCode, result.TimedOut, result.Stdout ?? "", result.Stderr ?? ""));
            });
        return new(files, invocations, failures);
    }

    private sealed record ToolInvocation(string Input, string[] Arguments);
}
