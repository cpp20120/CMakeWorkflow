using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record EventPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<JsonElement> Events);
