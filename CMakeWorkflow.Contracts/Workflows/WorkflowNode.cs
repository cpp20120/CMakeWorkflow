using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>One DAG node with kind-specific execution and evidence requirements.</summary>
/// <remarks>
/// Kinds are inspect, configure, build, invoke, run, profile and ctest. IDs must be unique.
/// Run/profile inherit context and target from a build node through From; other kinds use Context.
/// DependsOn, From and producer artifact references contribute dependencies. A node runs only after its prerequisites succeed.
/// Collections must be non-null; construction does not validate kind-specific combinations.
/// After interruption, a persisted running intent is uncertain evidence and must not be blindly re-executed.
/// </remarks>
public sealed class WorkflowNode
{
    /// <summary>Nonempty node identifier; unique in the document.</summary>
    public string Id { get; set; } = "";
    /// <summary>Execution kind: inspect, configure, build, invoke, run, profile or ctest.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Context key for context-based nodes; null for run/profile, which inherit it through From.</summary>
    public string? Context { get; set; }
    /// <summary>Native target for build/invoke; null for other kinds.</summary>
    public string? Target { get; set; }
    /// <summary>Build-node ID supplying the executable to run/profile; null for other kinds.</summary>
    public string? From { get; set; }
    /// <summary>Explicit prerequisite IDs; empty means no explicit prerequisites.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.DependsOn exposes List<string>; retain its concrete CLR property type for workflow builders and direct DTO construction.")]
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.DependsOn supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public List<string> DependsOn { get; set; } = [];
    /// <summary>Literal process arguments for run/profile, without shell parsing or NUL characters.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Arguments exposes List<string>; retain its concrete CLR property type for workflow builders and direct DTO construction.")]
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Arguments supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public List<string> Arguments { get; set; } = [];
    /// <summary>Optional execution directory; null uses the executor default. Relative paths use the manifest base.</summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>Environment overrides for execution; keys cannot contain equals or NUL, and values cannot contain NUL.</summary>
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Environment supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>Profile-extension options; must be empty for other kinds. Values are literal strings without NUL.</summary>
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Options supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public Dictionary<string, string> Options { get; set; } = [];

    // Explicit output directory relative to the native build tree (invoke only).
    /// <summary>Invoke-only output directory relative to the build tree, without empty, dot or parent segments; null uses default handling.</summary>
    public string? HarnessDirectory { get; set; }
    /// <summary>CTest name filter, or null for no name filter; valid only for ctest.</summary>
    public string? TestRegex { get; set; }
    /// <summary>CTest label filter, or null for no label filter; valid only for ctest.</summary>
    public string? LabelRegex { get; set; }
    /// <summary>Named scheduling conflict keys within the run; empty requests no additional named resources.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Resources exposes List<string>; retain its concrete CLR property type for workflow builders and direct DTO construction.")]
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Resources supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public List<string> Resources { get; set; } = [];
    /// <summary>Whether this node requires exclusive admission relative to other active nodes in the run.</summary>
    public bool Exclusive { get; set; }

    /// <summary>Positive finite execution timeout in seconds, at most 30 days; null imposes no node timeout.</summary>
    public double? TimeoutSeconds { get; set; }

    // Optional schema-v2 evidence. A reference adds an ordinary DAG dependency.
    /// <summary>Input evidence checked before execution; producer references also add dependencies.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Inputs exposes List<WorkflowArtifactInput>; retain its concrete CLR property type for workflow builders and direct DTO construction.")]
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Inputs supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public List<WorkflowArtifactInput> Inputs { get; set; } = [];
    /// <summary>Named files to validate and hash after successful execution; empty declares no outputs.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Outputs exposes List<WorkflowArtifactOutput>; retain its concrete CLR property type for workflow builders and direct DTO construction.")]
    [SuppressMessage("Usage", "CA2227", Justification = "Contracts SDK authoring member CMakeWorkflow.Contracts.Workflows.WorkflowNode.Outputs supports whole-collection assignment in WorkflowDocument/WorkflowNode object initializers; retain its public setter.")]
    public List<WorkflowArtifactOutput> Outputs { get; set; } = [];
    /// <summary>Execution requirements, or null for no explicit requirements.</summary>
    public WorkflowExecutionConstraints? Constraints { get; set; }
}
