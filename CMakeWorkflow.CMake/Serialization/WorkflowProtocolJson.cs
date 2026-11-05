using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowPlanView))]
[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(TargetResolutionView))]
public partial class WorkflowProtocolJson : JsonSerializerContext;
