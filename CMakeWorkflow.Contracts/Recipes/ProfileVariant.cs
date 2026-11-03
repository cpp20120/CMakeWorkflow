using System.Text.Json.Serialization;

namespace CMakeWorkflow.Profiling;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileVariant(string Name, string BuildDirectory);
