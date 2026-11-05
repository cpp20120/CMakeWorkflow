using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

/// <summary>Filter workspace operations with bounded keyset pagination.</summary>
/// <remarks>New admissions are excluded from subsequent pages, but state changes are not snapshot-isolated.</remarks>
public sealed record OperationQuery(int Limit = 50, string? State = null,
    string? Domain = null, string? Cursor = null);

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationPage(int SchemaVersion, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<OperationSnapshot> Items, string? NextCursor);
