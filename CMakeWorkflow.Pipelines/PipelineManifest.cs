using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Pipelines;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PipelineDocument))]
internal sealed partial class PipelineJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and writes pipeline definitions with NativeAOT-compatible JSON metadata.</summary>
/// <remarks>Parsing does not validate the graph, inspect paths, contact GitHub or execute anything.</remarks>
public static class PipelineManifest
{
    public static PipelineDocument ParseJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, PipelineJsonContext.Default.PipelineDocument)
               ?? throw new JsonException("Pipeline document must be an object.");
    }

    public static string ToJson(PipelineDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, PipelineJsonContext.Default.PipelineDocument);
    }
}