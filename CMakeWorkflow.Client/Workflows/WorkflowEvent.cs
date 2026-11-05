using System.Text.Json;

namespace CMakeWorkflow.Client.Workflows;

public sealed record WorkflowEvent(long Sequence, string Kind, JsonElement Data);
