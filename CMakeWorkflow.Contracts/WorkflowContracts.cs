using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowDocument
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, WorkflowContext> Contexts { get; set; } = [];
    public List<WorkflowNode> Nodes { get; set; } = [];
}

public sealed class WorkflowContext
{
    public string BuildDirectory { get; set; } = "";
    public string? Configuration { get; set; }
    public string? ConfigurePreset { get; set; }
    public string? SourceDirectory { get; set; }
    public string? Mode { get; set; }
    // An identity expectation, never a -G override or duplicated build setting.
    public string? Generator { get; set; }
    // strict (default) or allowUnverified: explicit adoption of unknown preset provenance.
    public string ProvenancePolicy { get; set; } = "strict";
    // Opt-in. Candidate IDs are resolved on the execution host; not executable paths.
    public WorkflowToolchainBinding? Toolchain { get; set; }
}

public sealed class WorkflowNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Context { get; set; }
    public string? Target { get; set; }
    public string? From { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = [];
    public Dictionary<string, string> Options { get; set; } = [];
    // Explicit output directory relative to the native build tree (invoke only).
    public string? HarnessDirectory { get; set; }
    public string? TestRegex { get; set; }
    public string? LabelRegex { get; set; }
    public List<string> Resources { get; set; } = [];
    public bool Exclusive { get; set; }
    public double? TimeoutSeconds { get; set; }
}

// A request for toolchain binding, deliberately independent of Windows discovery DTOs.
// The Host (or CLI) resolves IDs afresh against its own inventory. Never trust client paths.
public sealed record WorkflowToolchainBinding(
    [property: JsonPropertyName("cmakeCandidateId")] string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null, string? SdkCandidateId = null,
    string? EnvironmentProfileId = null, string Generator = "Ninja",
    string HostArchitecture = "x64", string TargetArchitecture = "x64");
