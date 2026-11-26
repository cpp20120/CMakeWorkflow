using CMakeWorkflow.Contracts.Workflows;
using System.Collections.Concurrent;
using System.ComponentModel;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.CMake.Quality;

/// <summary>Formats an explicitly selected local source workspace without requiring a CMake build tree.</summary>
public static partial class SourceFormatting
{
    public static async Task<FormatResult> RunAsync(FormatRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Jobs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Jobs, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);
        if (!Enum.IsDefined(request.Languages) || !Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Selection) ||
            !Enum.IsDefined(request.ToolPolicy) || (request.Files is not null && request.Selection != FormatSelection.Project) ||
            (request.Selection == FormatSelection.Staged && request.Mode == FormatMode.Apply) ||
            (request.Mode == FormatMode.Diff && request.Languages != FormatLanguage.CMake) ||
            (request.Executable is not null && (request.Languages == FormatLanguage.All || request.ToolPolicy == ToolResolutionPolicy.ManagedOnly)))
        {
            throw new ArgumentException("Incompatible formatter language, mode, selection or tool policy.", nameof(request));
        }

        string root = BuildTreeOwnership.CanonicalPath(request.ProjectDirectory);
        string[] files = [];
        ConcurrentDictionary<string, FormatFile> results = new(WorkflowPlanner.PathComparer);
        ConcurrentQueue<FormatInvocation> invocations = new();
        List<FormatToolIdentity> tools = [];
        string activeTool = "selection";
        bool failed = false;
        try
        {
            request.ReadGuard?.Invoke(root);
            request.ReadGuard?.Invoke(Path.Combine(root, "CMakeLists.txt"));
            if (!File.Exists(Path.Combine(root, "CMakeLists.txt")))
            {
                throw new InvalidDataException("Select a project directory containing CMakeLists.txt.");
            }
            files = await FormatFiles.SelectAsync(root, request, ct);
            string[] cpp = [.. files.Where(file => FormatFiles.Language(file) == FormatLanguage.Cpp)];
            string[] cmake = [.. files.Where(file => FormatFiles.Language(file) == FormatLanguage.CMake)];

            activeTool = "clang-format";
            string? clang = cpp.Length == 0 ? null : ResolveClang(root, request);
            if (clang is not null)
            {
                tools.Add(new("clang-format", clang, request.Executable is null ? "system" : "explicit", null, null));
            }
            activeTool = "cmakefmt";
            if (cmake.Length != 0)
            {
                FormatToolIdentity formatter = await ResolveCMakeAsync(root, request, ct);
                tools.Add(formatter);
                failed = !await RunCMakeAsync(root, request, formatter, cmake, results, invocations, ct);
            }
            if (clang is not null)
            {
                activeTool = "clang-format";
                failed |= !await RunClangAsync(root, request, clang, cpp, results, invocations, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result(FormatStatus.Cancelled);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                                      or Win32Exception or TimeoutException or PlatformNotSupportedException)
        {
            invocations.Enqueue(new(activeTool, "prepare", null, error.Message));
            failed = true;
        }

        FormatStatus status = failed || files.Any(file => !results.ContainsKey(file)) ||
            results.Values.Any(file => file.Status is FormatStatus.Failed or FormatStatus.Unprocessed) ? FormatStatus.Failed :
            results.Values.Any(file => file.Status == FormatStatus.FormattingRequired) ? FormatStatus.FormattingRequired :
            results.Values.Any(file => file.Status == FormatStatus.Formatted) ? FormatStatus.Formatted : FormatStatus.Clean;
        return Result(status);

        FormatResult Result(FormatStatus status) => new(status,
            [.. files.Select(file => results.TryGetValue(file, out FormatFile? value) ? value : new(FormatFiles.Relative(root, file),
                status == FormatStatus.Cancelled ? FormatStatus.Cancelled : FormatStatus.Unprocessed))],
            [.. invocations])
        {
            Tools = [.. tools]
        };
    }

    private static string ResolveClang(string root, FormatRequest request)
    {
        return request.ToolPolicy == ToolResolutionPolicy.ManagedOnly
            ? throw new InvalidDataException("There is no managed clang-format release. Select CMake only or allow a system clang-format.")
            : request.Executable is { } path ? Path.GetFullPath(path, root) :
                new SystemToolingRuntime().Resolve("clang-format", "") ?? throw new InvalidDataException("clang-format was not found on PATH.");
    }

    private static async Task<FormatToolIdentity> ResolveCMakeAsync(string root, FormatRequest request, CancellationToken ct)
    {
        if (request.Executable is { } path)
        {
            return new("cmakefmt", Path.GetFullPath(path, root), "explicit", null, null);
        }

        ToolingService tooling = new(new SystemToolingRuntime(root), new VerifiedCMakeInstaller());
        ToolingPlan plan = await tooling.PlanAsync(["cmakefmt"], PackageManagerKind.None, false, null, ct, resolutionPolicy: request.ToolPolicy);
        ToolStatus status = plan.Tools.Single();
        return status is { Installed: true, Command: { } executable } ? new("cmakefmt", executable, status.Origin ?? "system", status.Version, status.Sha256) :
            throw new InvalidDataException($"cmakefmt is unavailable. {status.Diagnostic} Review tooling plan --profile cmakefmt --tool-policy ManagedOnly.");
    }
}
