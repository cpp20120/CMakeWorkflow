using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.History.Models;

public sealed record HistoryImportResult(int SchemaVersion, int Imported, int Unchanged, int Skipped)
{
    [JsonIgnore]
    public IReadOnlyList<IndexedRunIdentity> RunIdentities { get; init; } = [];
}
