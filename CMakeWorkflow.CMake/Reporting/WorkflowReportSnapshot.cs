#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record WorkflowReportSnapshot(
    int SchemaVersion,
    string RunDirectory,
    string Revision,
    WorkflowReport Report);
