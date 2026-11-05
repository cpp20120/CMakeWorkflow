using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Profiling;

[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(JsonNode))]
internal partial class ProfileJson : JsonSerializerContext;