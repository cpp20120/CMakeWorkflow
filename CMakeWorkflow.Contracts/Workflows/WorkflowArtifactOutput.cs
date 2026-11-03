using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowArtifactOutput
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    // manifest: relative to the manifest; run: relative to the current run directory.
    public string Root { get; set; } = "manifest";
    public bool Fresh { get; set; }
}
