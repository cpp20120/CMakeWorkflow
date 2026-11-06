using System.Text.Json;

namespace CMakeWorkflow.Client.Workflows;

/// <summary>Verified operation-bound terminal CMake report.</summary>
/// <param name="SchemaVersion">Response schema version.</param>
/// <param name="RunDirectory">Absolute Host run directory.</param>
/// <param name="Revision">SHA-256 of report bytes committed at durable finalization; not a numeric CAS revision.</param>
/// <param name="Report">Detached report JSON, including failed or canceled terminal results.</param>
public sealed record OperationReportSnapshot(
    int SchemaVersion,
    string RunDirectory,
    string Revision,
    JsonElement Report);
