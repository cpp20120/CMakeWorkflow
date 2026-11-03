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
    // Optional schema-v2 evidence. A reference adds an ordinary DAG dependency.
    public List<WorkflowArtifactInput> Inputs { get; set; } = [];
    public List<WorkflowArtifactOutput> Outputs { get; set; } = [];
    public WorkflowExecutionConstraints? Constraints { get; set; }
}

public sealed class WorkflowExecutionConstraints
{
    public string? CpuSet { get; set; }
    public WorkflowProfilerRequirement? Profiler { get; set; }
}

public sealed class WorkflowProfilerRequirement
{
    public string Backend { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? EventSource { get; set; }
}

public sealed class WorkflowArtifactInput
{
    public string? Path { get; set; }
    public string? Sha256 { get; set; }
    public string? From { get; set; }
    public string? Artifact { get; set; }
    public bool Snapshot { get; set; }
}

public sealed class WorkflowArtifactOutput
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    // manifest: relative to the manifest; run: relative to the current run directory.
    public string Root { get; set; } = "manifest";
    public bool Fresh { get; set; }
}

// A request for toolchain binding, deliberately independent of Windows discovery DTOs.
// The Host (or CLI) resolves IDs afresh against its own inventory. Never trust client paths.
public sealed record WorkflowToolchainBinding(
    [property: JsonPropertyName("cmakeCandidateId")] string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null, string? SdkCandidateId = null,
    string? EnvironmentProfileId = null, string Generator = "Ninja",
    string HostArchitecture = "x64", string TargetArchitecture = "x64");
