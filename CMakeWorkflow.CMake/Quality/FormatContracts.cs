using CMakeWorkflow.Tooling.Tools;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Quality;

public enum FormatMode { Check, Apply, Diff }

public enum FormatSelection { Project, Changed, Staged }

public sealed record FormatRequest(string ProjectDirectory)
{
    public FormatLanguage Languages { get; init; } = FormatLanguage.All;
    public FormatMode Mode { get; init; }
    public FormatSelection Selection { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    public string? Include { get; init; }
    public string? Exclude { get; init; }
    public string? Executable { get; init; }
    public ToolResolutionPolicy ToolPolicy { get; init; } = ToolResolutionPolicy.AllowSystem;
    internal Action<string>? ReadGuard { get; init; }
    public int Jobs { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);
    public int BatchSize { get; init; } = 128;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

