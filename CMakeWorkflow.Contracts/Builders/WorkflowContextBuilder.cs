namespace CMakeWorkflow;

public sealed class WorkflowContextBuilder(WorkflowContext context)
{
    public WorkflowContextBuilder BuildDirectory(string path)
    {
        context.BuildDirectory = path;
        return this;
    }

    public WorkflowContextBuilder SourceDirectory(string path)
    {
        context.SourceDirectory = path;
        return this;
    }

    public WorkflowContextBuilder Configuration(string name)
    {
        context.Configuration = name;
        return this;
    }

    public WorkflowContextBuilder ConfigurePreset(string name)
    {
        context.ConfigurePreset = name;
        return this;
    }

    public WorkflowContextBuilder Generator(string name)
    {
        context.Generator = name;
        return this;
    }

    public WorkflowContextBuilder AdoptExisting()
    {
        context.Mode = "adopt";
        return this;
    }

    public WorkflowContextBuilder Managed()
    {
        context.Mode = "managed";
        return this;
    }

    public WorkflowContextBuilder Observe()
    {
        context.Mode = "observe";
        return this;
    }

    public WorkflowContextBuilder AllowUnverified()
    {
        context.ProvenancePolicy = "allowUnverified";
        return this;
    }

    public WorkflowContextBuilder Toolchain(WorkflowToolchainBinding binding)
    {
        context.Toolchain = binding;
        return this;
    }
}
