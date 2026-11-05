using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

/// <summary>
/// An operation-bound terminal report. Report is the existing CMakeWorkflow
/// report JSON; Revision is the SHA-256 digest committed by the remote Host.
/// </summary>
public sealed record OperationReportSnapshot(
    int SchemaVersion,
    string RunDirectory,
    string Revision,
    JsonElement Report);
