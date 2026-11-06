using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

/// <summary>Workspace operation filter and keyset cursor.</summary>
/// <remarks>
/// A cursor excludes later admissions, but state transitions are not snapshot-isolated.
/// </remarks>
/// <param name="Limit">Page size, from 1 to 200; defaults to 50.</param>
/// <param name="State">Exact durable state filter, or null for all states.</param>
/// <param name="Domain">Exact domain filter, or null for all domains.</param>
/// <param name="Cursor">Opaque Host cursor from the preceding page, or null for the first page; reuse matching filters.</param>
public sealed record OperationQuery(int Limit = 50, string? State = null,
    string? Domain = null, string? Cursor = null);

/// <summary>Bounded page of durable operations, newest first.</summary>
/// <param name="SchemaVersion">Response schema version.</param>
/// <param name="Items">Non-null operation list.</param>
/// <param name="NextCursor">Opaque next-page cursor, or null when enumeration is exhausted.</param>
[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Workflows.OperationPage(Items) participates in CMakeWorkflow.Client.Workflows.OperationService.ListAsync; retain these concrete collection parameter types.")]
public sealed record OperationPage(int SchemaVersion, [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Workflows.OperationPage.Items (List<OperationSnapshot>) is exposed through CMakeWorkflow.Client.Workflows.OperationService.ListAsync; retain this Client SDK property signature.")]
List<OperationSnapshot> Items, string? NextCursor);
