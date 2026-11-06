// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Workflows;
#pragma warning restore IDE0130

/// <summary>Requested CMake build-tree identity and ownership policy.</summary>
/// <remarks>
/// A context describes one native build tree. Planning observes its current state; this DTO does not configure it.
/// Managed mode permits explicit configure nodes; adopt mode accepts an existing tree under the provenance policy.
/// Null optional fields mean no explicit request or expectation, rather than an empty string.
/// </remarks>
public sealed class WorkflowContext
{
    /// <summary>Native build-tree path, resolved relative to the manifest when not absolute.</summary>
    public string BuildDirectory { get; set; } = "";
    /// <summary>CMake configuration, or null for the context default; required where multi-config resolution needs it.</summary>
    public string? Configuration { get; set; }
    /// <summary>Preset name for explicit managed configuration, or null when none is requested.</summary>
    public string? ConfigurePreset { get; set; }
    /// <summary>Source-tree path, or null when inferred from available build-tree/preset evidence.</summary>
    public string? SourceDirectory { get; set; }

    /// <summary>observe, adopt or managed; null lets planning infer managed from configure evidence, otherwise observe.</summary>
    public string? Mode { get; set; }

    // An identity expectation, never a -G override or duplicated build setting.
    /// <summary>Expected generator identity, or null for no expectation; never a -G override.</summary>
    public string? Generator { get; set; }

    // strict (default) or allowUnverified: explicit adoption of unknown preset provenance.
    /// <summary>strict by default; allowUnverified explicitly permits adoption of unknown preset provenance.</summary>
    public string ProvenancePolicy { get; set; } = "strict";

    // Opt-in. Candidate IDs are resolved on the execution host; not executable paths.
    /// <summary>Host-resolved toolchain binding, or null to use ambient tooling.</summary>
    public WorkflowToolchainBinding? Toolchain { get; set; }
}
