using System.Text.Json.Serialization;

namespace CMakeWorkflow.Sources;

// NativeAOT Host must not rely on reflection-based JSON serialization in the
// Lore storage adapter. Keep the exact default property names and ordering.
[JsonSerializable(typeof(LoreBindings))]
internal sealed partial class SourceSnapshotJson : JsonSerializerContext;
