using System.Text.Json.Serialization;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkflowDocument))]
[JsonSerializable(typeof(WorkflowReport))]
[JsonSerializable(typeof(WorkflowToolchainIdentity))]
[JsonSerializable(typeof(WorkflowToolchainBinding))]
[JsonSerializable(typeof(NodeResult))]
[JsonSerializable(typeof(ObservedBuildContext))]
[JsonSerializable(typeof(ConfigureProvenance))]
[JsonSerializable(typeof(List<string>))]
public partial class WorkflowJson : JsonSerializerContext;