using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed record WorkflowArtifactView(string Type, string Path, string? Sha256);
