using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Client.Workflows;

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record EventPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<JsonElement> Events);
