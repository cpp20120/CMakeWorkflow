using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

// Public .NET SDK DTOs outside the shared recipe/model contracts.
// TypeScript uses the same JSON field names.
public sealed record PreparedWorkflow(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
