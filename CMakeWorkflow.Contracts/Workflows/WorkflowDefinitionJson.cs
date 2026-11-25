using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Source-generated JSON contract for the existing workflow manifest.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkflowDocument))]
public partial class WorkflowDefinitionJson : JsonSerializerContext;
