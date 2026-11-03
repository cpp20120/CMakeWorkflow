using System.Text.Json.Serialization;

namespace CMakeWorkflow;

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
