using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Client.Experiments;

/// <summary>A controller plan read from its approved SHA-256 and ready to stage on its workspace.</summary>
public sealed class PreparedExperimentPlan
{
    private PreparedExperimentPlan(ExperimentPlan plan, string id, string planPath)
    {
        Repository = plan.Repository;
        RequestedRef = plan.RequestedRef;
        Commit = plan.Commit;
        Host = plan.Host;
        PlanPath = planPath;
        ArchivePath = Path.Combine(Path.GetDirectoryName(planPath)!, "source.zip");
        Stage = new(id, plan.SourceSnapshotId, plan.ManifestSha256, plan.Manifest, [])
        {
            Bundle = plan.Bundle
        };
        Placement = plan.Placement;
        DependencyPlanSha256 = plan.DependencyPlanSha256;
    }

    internal static PreparedExperimentPlan FromPlan(ExperimentPlan plan, string id, string path) =>
        plan is { SchemaVersion: 2, Bundle: not null, Files.Count: 0 }
            ? new(plan, id, path)
            : throw new InvalidDataException("The SDK requires a schema-v2 experiment plan with a source archive.");

    public string Id => Stage.ExperimentId;
    public string Repository { get; }
    public string RequestedRef { get; }
    public ExperimentHost Host { get; }
    public string PlanPath { get; }
    public string Manifest => Stage.Manifest;
    public int SourceFileCount => Stage.Bundle!.FileCount;
    public string Commit { get; }
    public string Workspace => Host.Workspace;
    public string ArchivePath { get; }
    public string SourceSnapshotId => Stage.SourceSnapshotId;
    public string ManifestSha256 => Stage.ManifestSha256;
    public string RequestId => "experiment-sdk-" + Id;
    public ExperimentPlacement? Placement { get; }
    public string? DependencyPlanSha256 { get; }
    internal ExperimentStageRequest Stage { get; }
}
