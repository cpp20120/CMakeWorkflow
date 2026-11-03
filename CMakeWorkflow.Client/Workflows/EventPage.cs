using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record EventPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,
    List<JsonElement> Events);
