#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class WorkflowArtifactOutput
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    // manifest: relative to the manifest; run: relative to the current run directory.
    public string Root { get; set; } = "manifest";
    public bool Fresh { get; set; }
}
