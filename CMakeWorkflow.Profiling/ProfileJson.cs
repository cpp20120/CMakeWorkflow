using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;

namespace CMakeWorkflow.Profiling;

[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(JsonNode))]
internal partial class ProfileJson : JsonSerializerContext;