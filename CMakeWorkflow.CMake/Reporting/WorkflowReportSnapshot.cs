using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record WorkflowReportSnapshot(
    int SchemaVersion,
    string RunDirectory,
    string Revision,
    WorkflowReport Report);
