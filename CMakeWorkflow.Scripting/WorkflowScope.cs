namespace CMakeWorkflow.Scripting;

/// <summary>A stable fragment instance namespace in an existing authoring scope.</summary>
/// <remarks>Each segment uses letters, digits, underscores or hyphens. Dots delimit nested segments.</remarks>
public sealed class WorkflowScope
{
    internal WorkflowScope(Workflow workflow, string instanceId)
    {
        Workflow = workflow;
        InstanceId = instanceId;
    }

    /// <summary>The original workflow. References from nested scopes share its dependency graph.</summary>
    public Workflow Workflow { get; }

    /// <summary>The qualified identity for this instance.</summary>
    public string InstanceId { get; }

    /// <summary>Qualifies a context or node ID within this instance.</summary>
    public string Id(string localId) => InstanceId + "." + Segment(localId);

    /// <summary>Creates a nested instance without registering any nodes.</summary>
    public WorkflowScope Scope(string instance) => new(Workflow, Id(instance));

    internal static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
            ? throw new ArgumentException("Fragment identity segments use ASCII letters, digits, underscores or hyphens.", nameof(value))
            : value;
    }
}
