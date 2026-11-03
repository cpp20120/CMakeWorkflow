using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed class WorkflowReport
{
    public int SchemaVersion { get; set; } = 1;
    public string Workflow { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public List<NodeResult> Nodes { get; set; } = [];
    public string? Error { get; set; }
    public List<WorkflowFinding> Findings { get; set; } = [];
    public int ResumeCount { get; set; }
    public string? RecoveryPhase { get; set; }
    public List<WorkflowReconciliation> Reconciliations { get; set; } = [];
    public HostEnvironment? Environment { get; set; }
    public HostEnvironment? EnvironmentAtEnd { get; set; }
    public List<BuildEnvironment> BuildEnvironments { get; set; } = [];
    public List<WorkflowToolchainIdentity> Toolchains { get; set; } = [];
}
