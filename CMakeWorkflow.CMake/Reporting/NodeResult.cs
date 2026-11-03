using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed class NodeResult
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Dependencies { get; set; } = [];
    public string BuildDirectory { get; set; } = "";
    public string? Configuration { get; set; }
    public string Target { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public double? ProcessDurationSeconds { get; set; }
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? ExecutableSha256 { get; set; }
    public List<string> Arguments { get; set; } = [];
    public string? EnvironmentOverrideHash { get; set; }
    public string? ToolchainFingerprint { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? Error { get; set; }
    public RequestedBuildContext? RequestedContext { get; set; }
    public ObservedBuildContext? ObservedContext { get; set; }
    public ObservedBuildContext? ObservationAfter { get; set; }
    public NativeBuildResult? BuildVerification { get; set; }
    public ArtifactReference? Artifact { get; set; }
    public ConfigureProvenance? Configure { get; set; }
    public BuildContextSessionState? SessionBefore { get; set; }
    public BuildContextSessionState? SessionAfter { get; set; }
    public string ExperimentResult { get; set; } = "NotRun";
    public Dictionary<string, string> Outputs { get; set; } = [];
    public List<WorkflowArtifactEvidence> DeclaredArtifacts { get; set; } = [];
    public Dictionary<string, string> ExecutionConditions { get; set; } = [];
}
