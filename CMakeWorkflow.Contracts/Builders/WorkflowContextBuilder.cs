using CMakeWorkflow.Contracts.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Builders;
#pragma warning restore IDE0130

/// <summary>Mutable fluent adapter over a workflow DTO.</summary>
/// <remarks>
/// Methods only modify the supplied DTO and return this builder. They perform no execution or persistence.
/// The caller owns the DTO; planning remains authoritative for semantic validation.
/// </remarks>
public sealed class WorkflowContextBuilder(WorkflowContext context)
{
    /// <summary>Sets the native build-tree path; resolution and validation happen during planning.</summary>
    public WorkflowContextBuilder BuildDirectory(string path)
    {
        context.BuildDirectory = path;
        return this;
    }

    /// <summary>Sets the source-tree path for managed configuration or identity checks.</summary>
    public WorkflowContextBuilder SourceDirectory(string path)
    {
        context.SourceDirectory = path;
        return this;
    }

    /// <summary>Sets the requested CMake configuration.</summary>
    public WorkflowContextBuilder Configuration(string name)
    {
        context.Configuration = name;
        return this;
    }

    /// <summary>Sets the preset name; does not configure the tree.</summary>
    public WorkflowContextBuilder ConfigurePreset(string name)
    {
        context.ConfigurePreset = name;
        return this;
    }

    /// <summary>Sets a generator identity expectation, not a command-line override.</summary>
    public WorkflowContextBuilder Generator(string name)
    {
        context.Generator = name;
        return this;
    }

    /// <summary>Selects adoption of an existing build tree.</summary>
    public WorkflowContextBuilder AdoptExisting()
    {
        context.Mode = "adopt";
        return this;
    }

    /// <summary>Selects managed mode for explicit configuration nodes.</summary>
    public WorkflowContextBuilder Managed()
    {
        context.Mode = "managed";
        return this;
    }

    /// <summary>Selects observation without adopting mutable build ownership.</summary>
    public WorkflowContextBuilder Observe()
    {
        context.Mode = "observe";
        return this;
    }

    /// <summary>Explicitly permits unknown preset provenance during adoption.</summary>
    public WorkflowContextBuilder AllowUnverified()
    {
        context.ProvenancePolicy = "allowUnverified";
        return this;
    }

    /// <summary>Sets Host-local candidate identity requests; does not resolve executable paths.</summary>
    public WorkflowContextBuilder Toolchain(WorkflowToolchainBinding binding)
    {
        context.Toolchain = binding;
        return this;
    }
}
