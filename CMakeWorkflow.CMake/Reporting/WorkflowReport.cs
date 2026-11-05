using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.CMake.Recovery;
namespace CMakeWorkflow.CMake.Reporting;

public sealed class WorkflowReport
{
    public int SchemaVersion { get; set; } = 1;
    public string Workflow { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<NodeResult> Nodes { get; set; } = [];
    public string? Error { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowFinding> Findings { get; set; } = [];
    public int ResumeCount { get; set; }
    public string? RecoveryPhase { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowReconciliation> Reconciliations { get; set; } = [];
    public HostEnvironment? Environment { get; set; }
    public HostEnvironment? EnvironmentAtEnd { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<BuildEnvironment> BuildEnvironments { get; set; } = [];
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowToolchainIdentity> Toolchains { get; set; } = [];
}
