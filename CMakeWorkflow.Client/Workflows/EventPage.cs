using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Client.Workflows;

/// <summary>Bounded operation journal page using an exclusive sequence cursor.</summary>
/// <remarks>
/// An empty page does not imply terminal state. The journal is audit evidence, not a recovery checkpoint.
/// </remarks>
/// <param name="OperationId">Operation whose journal was read.</param>
/// <param name="AfterSequence">Exclusive input cursor.</param>
/// <param name="NextSequence">Last returned sequence, or the unchanged input cursor for an empty page.</param>
/// <param name="HasMore">Whether another event was found after this page.</param>
/// <param name="Events">Non-null event list, in strictly increasing sequence order.</param>
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record EventPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<JsonElement> Events);
