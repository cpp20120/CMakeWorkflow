using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Developer;

public sealed record ProjectLocation(string StartingDirectory)
{
    /// <summary>When set, discover exactly this path, relative to StartingDirectory if necessary.</summary>
    public string? ProjectDirectory { get; init; }
}

public sealed record DiscoveredProject(string Directory, string ManifestPath);

public sealed record CreateProjectRequest(string Sdk, string Name, string Directory)
{
    public string? SdkVersion { get; init; }
    public string? EnvironmentLockSha256 { get; init; }
    public bool Variants { get; init; } = true;
    public bool InitializeDefaultLocks { get; init; } = true;
}

public sealed record CreatedProject(string Directory);

public sealed record ConfigurationSelection
{
    public string? Kit { get; init; }
    public string? Profile { get; init; }
    public bool Matrix { get; init; }
    public string? Overrides { get; init; }
    public string? Variant { get; init; }
}

public sealed record ProjectLockPlan(string Sha256, string Path, ReadOnlyMemory<byte> Json);

public sealed record ProjectVariant(string Name, string Id, string BuildDirectory, string Configuration,
    string Generator, string? EnvironmentLockSha256, bool Testing);

public sealed record ConfigurationPlan(ProjectLockPlan Lock, IReadOnlyList<ProjectVariant> Variants)
{
    public string? KitLockSha256 { get; init; }
}

public enum DeveloperDiagnosticCode
{
    SdkLockApprovalRequired,
    KitLockApprovalRequired,
    KitDependencyUnsupported,
    ConfigurationLockApprovalRequired,
    ConfigurationUnavailable,
    ExternalDependencyUnverified,
    ToolingUnavailable,
    EnvironmentLockUnavailable,
    DependencyPlanApprovalRequired,
    HostEnvironmentUnverified
}

public sealed record DeveloperDiagnostic(DeveloperDiagnosticCode Code, string Message, string? Remediation = null);

public sealed record ProjectInspection(DiscoveredProject Project, SdkDescriptor Sdk, ProjectLockPlan SdkLock,
    ConfigurationPlan? Configuration, IReadOnlyList<DeveloperDiagnostic> Diagnostics)
{
    public SelectedEngineeringKit? SelectedKit { get; init; }
    public EngineeringKitPlan? Kit { get; init; }
    public ToolingPlan? LocalTooling { get; init; }
    public ProjectLockPlan? VcpkgPlan { get; init; }
}

public sealed record RestoreRequest(ProjectLocation Location)
{
    public ConfigurationSelection Configuration { get; init; } = new();
    public bool ProbeLocalTools { get; init; }
    public string? SdkApprovalSha256 { get; init; }
    public string? ConfigurationApprovalSha256 { get; init; }
}

public enum DeveloperOperationKind { Configure, Build, Test, Run, Benchmark, Profile, Pack }

public sealed record DeveloperOperationRequest(ProjectLocation Location, DeveloperOperationKind Kind)
{
    public ConfigurationSelection Configuration { get; init; } = new();
    public string? Target { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? TestRegex { get; init; }
    public string? LabelRegex { get; init; }
    public int Repeats { get; init; } = 5;
    public string? ProfileBackend { get; init; }
    public string? ProfileMode { get; init; }
}

public sealed record DeveloperOperationPlan(string ProjectDirectory, DeveloperOperationKind Kind, string ManifestName,
    string ManifestSha256, ReadOnlyMemory<byte> ManifestJson, ProjectLockPlan ConfigurationLock, IReadOnlyList<ProjectVariant> Variants)
{
    public SelectedEngineeringKit? SelectedKit { get; init; }
    public string? KitLockSha256 { get; init; }
    public string SdkId { get; init; } = "";
    public string SdkVersion { get; init; } = "";
}

public sealed record DeveloperExecutionRequest(DeveloperOperationRequest Operation, string ManifestSha256, string ClientRequestId)
{
    /// <summary>When supplied, reuse the reviewed selection even if the active project Kit has changed.</summary>
    public DeveloperOperationPlan? ReviewedPlan { get; init; }
    public int Jobs { get; init; } = 1;
    /// <summary>Parent results directory on the local workspace. Each request gets its own durable child directory.</summary>
    public string? ResultsDirectory { get; init; }
    public HistoryPolicy? HistoryPolicy { get; init; }
}

/// <summary>Explicit Host-side paths for an already staged project. Paths use Host syntax.</summary>
public sealed record StagedDeveloperExecution(string ManifestPath, string ResultsDirectory, string ClientRequestId)
{
    public string? SourceSnapshotId { get; init; }
    public int Jobs { get; init; } = 1;
    public HistoryPolicy? HistoryPolicy { get; init; }
}
