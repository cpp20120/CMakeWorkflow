using System.Text.Json.Serialization;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Recovery;

namespace CMakeWorkflow.CMake.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowPlanView))]
[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(TargetResolutionView))]
public partial class WorkflowProtocolJson : JsonSerializerContext;
