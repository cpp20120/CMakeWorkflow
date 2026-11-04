using System.Text.Json;

namespace CMakeWorkflow.Client;

/// <summary>
/// An operation-bound terminal report. Report is the existing CMakeWorkflow
/// report JSON; Revision is the SHA-256 digest committed by the remote Host.
/// </summary>
public sealed record OperationReportSnapshot(
    int SchemaVersion,
    string RunDirectory,
    string Revision,
    JsonElement Report);
