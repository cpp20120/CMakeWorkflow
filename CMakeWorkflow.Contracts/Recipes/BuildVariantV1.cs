using System.Text.Json.Serialization;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildVariantV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string BuildDirectory,
    string? Configuration = null);
