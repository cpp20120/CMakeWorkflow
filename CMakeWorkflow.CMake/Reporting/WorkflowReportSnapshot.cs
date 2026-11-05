namespace CMakeWorkflow.CMake.Reporting;

public sealed record WorkflowReportSnapshot(
    int SchemaVersion,
    string RunDirectory,
    string Revision,
    WorkflowReport Report);
