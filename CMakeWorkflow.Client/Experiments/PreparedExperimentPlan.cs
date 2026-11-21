using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Client.Experiments;

/// <summary>A controller plan read from its approved SHA-256 and ready to stage on its workspace.</summary>
public sealed class PreparedExperimentPlan
{
    internal PreparedExperimentPlan(string repository, string commit, string workspace, string archivePath,
        ExperimentStageRequest stage, ExperimentPlacement? placement, string? dependencyPlanSha256)
    {
        Repository = repository;
        Commit = commit;
        Workspace = workspace;
        ArchivePath = archivePath;
        Stage = stage;
        Placement = placement;
        DependencyPlanSha256 = dependencyPlanSha256;
    }

    public string Id => Stage.ExperimentId;
    public string Repository { get; }
    public string Commit { get; }
    public string Workspace { get; }
    public string ArchivePath { get; }
    public string SourceSnapshotId => Stage.SourceSnapshotId;
    public string ManifestSha256 => Stage.ManifestSha256;
    public string RequestId => "experiment-sdk-" + Id;
    public ExperimentPlacement? Placement { get; }
    public string? DependencyPlanSha256 { get; }
    internal ExperimentStageRequest Stage { get; }
}
